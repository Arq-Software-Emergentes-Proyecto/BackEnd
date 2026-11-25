using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Infrastructure.Authorization;
using BuildingFex.Api.Incidents.Application.CommandServices;
using BuildingFex.Api.Incidents.Application.Internal.CommandServices;
using BuildingFex.Api.Incidents.Domain.Model;
using BuildingFex.Api.Incidents.Domain.Model.Commands;
using BuildingFex.Api.Incidents.Domain.Model.ValueObjects;
using BuildingFex.Api.ResidentCare.Application;
using BuildingFex.Api.ResidentCare.Domain;
using BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;
using BuildingFex.Api.ResidentCare.Domain.Model.ValueObjects;
using BuildingFex.Api.ResidentCare.Domain.Services;
using BuildingFex.Api.ResidentCare.Infrastructure.Gemini;
using BuildingFex.Api.ResidentCare.Infrastructure.PlanQuota;
using BuildingFex.Api.ResidentCare.Infrastructure.RateLimiting;
using BuildingFex.Api.ResidentCare.Interfaces.Hubs;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Interfaces.Rest;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.ResidentCare.Interfaces.Rest;

/// <summary>
/// Support / Resident Care: MySQL support_chats + support_messages.
/// Escalado crea 2da fila kind=admin (el AI no cambia de kind); linked_chat_id une ambas.
/// El tenant y el usuario salen siempre del token (CallerContext), nunca del cliente.
/// </summary>
[ApiController]
[Route("api/v1/support")]
[Authorize(Policy = AuthorizationPolicies.AdminOrResident)]
public class SupportController(
    AppDbContext db,
    IGeminiClient gemini,
    GeminiRateLimiter rateLimiter,
    IHubContext<SupportHub> hub,
    IAiContextIndexer contextIndexer,
    IAiUsageQuota aiQuota,
    IPlanQuotaClient planQuota,
    IIncidentCommandService incidentCommands,
    IncidentTriageService incidentTriage,
    ILogger<SupportController> logger) : TenantScopedControllerBase
{
    private const int MaxMessageLength = 2000;
    private const int MaxHistoryTurns = 20;
    private static readonly TimeSpan TitleSummaryBudget = TimeSpan.FromSeconds(3);

    [HttpGet("chats")]
    public async Task<IActionResult> List(
        [FromQuery] string? kind,
        [FromQuery] string? since,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        var query = db.Set<SupportChat>().Where(c => c.OwnerAdminId == actor.TenantId);

        if (actor.IsAdmin)
            query = query.Where(c => c.Kind == (string.IsNullOrWhiteSpace(kind) ? SupportChat.KindAdmin : kind.Trim()));
        else
        {
            query = query.Where(c => c.ResidentId == actor.ExternalId);
            query = query.Where(c => c.Kind == (string.IsNullOrWhiteSpace(kind) ? SupportChat.KindAi : kind.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = ConversationStatus.Normalize(status);
            if (normalized is null)
                return BadRequest(new { code = "INVALID_STATUS", message = "Estado no válido.", allowed = ConversationStatus.All });
            query = normalized == ConversationStatus.Open
                ? query.Where(c => c.Status == ConversationStatus.Open || c.Status == null || c.Status == "")
                : query.Where(c => c.Status == normalized);
        }

        if (DateTimeOffset.TryParse(since, out var sinceAt))
            query = query.Where(c => c.UpdatedAt > sinceAt);

        var chats = await query
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        if (chats.Count == 0 && !string.IsNullOrWhiteSpace(since))
            return NoContent();

        return Ok(chats.Select(c => ToResource(c)).ToList());
    }

    /// <summary>US032: contadores de la bandeja del admin (badge de pendientes).</summary>
    [HttpGet("chats/summary")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        var rows = await db.Set<SupportChat>()
            .AsNoTracking()
            .Where(c => c.OwnerAdminId == actor.TenantId && c.Kind == SupportChat.KindAdmin)
            .GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(Func<string?, bool> match) => rows.Where(r => match(r.Status)).Sum(r => r.Count);
        return Ok(new
        {
            pending = Count(ConversationStatus.IsPending),
            answered = Count(s => s == ConversationStatus.Answered),
            closed = Count(s => s == ConversationStatus.Closed),
            total = rows.Sum(r => r.Count),
        });
    }

    /// <summary>US041 / CON11: consumo del tope mensual de consultas al asistente.</summary>
    [HttpGet("ai-usage")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> AiUsage(CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        var tenantAdmin = actor.Caller.TenantAdmin;
        // Portafolio: el tope es de toda la cuenta (todos sus edificios).
        var usage = await aiQuota.GetAsync(
            actor.Caller.BillingAdmin.ExternalId,
            planQuota.MonthlyAiQueryCap(tenantAdmin.SubscriptionPlanId, tenantAdmin.SubscriptionPaidUntil),
            ct);
        return Ok(new
        {
            period = usage.Period,
            used = usage.Used,
            cap = usage.Cap,
            remaining = usage.Remaining,
            filtered = usage.Filtered,
            residentDailyLimit = AnswerPolicy.ResidentDailyAiQueries,
            capReached = usage.CapReached,
            planId = tenantAdmin.SubscriptionPlanId,
            effectivePlanId = BuildingFex.Api.Billing.Domain.Model.SubscriptionPlans.EffectivePlanId(
                tenantAdmin.SubscriptionPlanId, tenantAdmin.SubscriptionPaidUntil, DateTimeOffset.UtcNow),
        });
    }

    [HttpPost("chats")]
    public async Task<IActionResult> Create([FromBody] CreateSupportChatResource resource, CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        var kind = (resource.Kind ?? SupportChat.KindAi).Trim().ToLowerInvariant();
        if (kind != SupportChat.KindAi)
            return BadRequest(new { code = "INVALID_KIND", message = "Solo se pueden crear chats kind=ai desde el residente." });

        if (actor.IsAdmin)
            return Forbid();

        var chat = SupportChat.CreateAi(
            $"ai-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            actor.TenantId,
            actor.ExternalId,
            actor.Name,
            string.IsNullOrWhiteSpace(resource.Topic) ? "Nuevo chat" : resource.Topic!);

        db.Set<SupportChat>().Add(chat);
        await db.SaveChangesAsync(ct);
        var resourceObj = ToResource(chat);
        await NotifyAsync(chat, resourceObj, ct);
        return StatusCode(StatusCodes.Status201Created, resourceObj);
    }

    [HttpGet("chats/{id}")]
    public async Task<IActionResult> GetById(
        string id,
        [FromQuery] string? afterMessageId,
        CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        var chat = await FindChatAsync(id, ct);
        if (chat is null) return NotFound(new { code = "CHAT_NOT_FOUND" });
        if (!CanAccess(actor, chat)) return ForbiddenTenant(actor.Caller, "chatId", id);

        if (!string.IsNullOrWhiteSpace(afterMessageId))
        {
            var after = chat.Messages.FirstOrDefault(m => m.ExternalId == afterMessageId.Trim());
            if (after is not null)
            {
                var newer = chat.Messages.Where(m => m.CreatedAt > after.CreatedAt || m.Id > after.Id).ToList();
                if (newer.Count == 0)
                    return NoContent();
                return Ok(ToResource(chat, newer));
            }
        }

        return Ok(ToResource(chat));
    }

    /// <summary>US032: el admin marca la conversación como pendiente o atendida (la cierra el residente).</summary>
    [HttpPatch("chats/{id}/status")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> UpdateStatus(
        string id,
        [FromBody] UpdateSupportChatStatusResource resource,
        CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        // Cerrar lo decide el residente al confirmar que se resolvió su duda; el admin solo marca pendiente / atendida.
        var status = ConversationStatus.Normalize(resource?.Status);
        if (status is null || status == ConversationStatus.Derived || status == ConversationStatus.Closed)
        {
            return BadRequest(new
            {
                code = "INVALID_STATUS",
                message = "Estado no válido.",
                allowed = new[] { ConversationStatus.Open, ConversationStatus.Answered },
            });
        }

        var chat = await FindChatAsync(id, ct);
        if (chat is null) return NotFound(new { code = "CHAT_NOT_FOUND" });
        if (!CanAccess(actor, chat)) return ForbiddenTenant(actor.Caller, "chatId", id);
        if (chat.Kind != SupportChat.KindAdmin)
            return BadRequest(new { code = "INVALID_KIND", message = "Solo las conversaciones de la bandeja admin cambian de estado." });
        if (chat.Status == ConversationStatus.Closed)
            return Conflict(new { code = "CHAT_CLOSED", message = "El residente cerró esta conversación." });

        chat.UpdateStatus(status);
        await db.SaveChangesAsync(ct);
        return await RespondAsync(chat, ct);
    }

    /// <summary>
    /// Informe 5.9.2: PATCH …/close. Solo el residente cierra su conversación, al confirmar que se resolvió su duda;
    /// desde ahí es de solo lectura (también el hilo derivado a la bandeja del admin).
    /// </summary>
    [HttpPatch("chats/{id}/close")]
    public async Task<IActionResult> Close(string id, CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        if (actor.IsAdmin)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "RESIDENT_CLOSES_CHAT",
                message = "La conversación la cierra el residente cuando confirma que se resolvió su duda.",
            });

        var chat = await FindChatAsync(id, ct);
        if (chat is null) return NotFound(new { code = "CHAT_NOT_FOUND" });
        if (!CanAccess(actor, chat)) return ForbiddenTenant(actor.Caller, "chatId", id);

        chat.Close();
        // El residente confirmó que se resolvió su duda: la conversación derivada a la administración también se cierra.
        if (!string.IsNullOrWhiteSpace(chat.LinkedChatId))
        {
            var linked = await FindChatAsync(chat.LinkedChatId, ct);
            if (linked is not null && linked.OwnerAdminId == chat.OwnerAdminId) linked.Close();
        }
        await db.SaveChangesAsync(ct);
        return await RespondAsync(chat, ct);
    }

    /// <summary>
    /// ReportarIncidente (Resident Care → Incidents): el residente convierte su consulta en una incidencia.
    /// Asunto y descripción salen del chat si no se envían; la incidencia queda ligada a la conversación,
    /// que recibe el aviso cuando se resuelve.
    /// </summary>
    [HttpPost("chats/{id}/incident")]
    [Authorize(Policy = AuthorizationPolicies.ResidentOnly)]
    public async Task<IActionResult> ReportIncident(
        string id,
        [FromBody] ReportIncidentFromChatResource? resource,
        CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        var chat = await FindChatAsync(id, ct);
        if (chat is null) return NotFound(new { code = "CHAT_NOT_FOUND" });
        if (!CanAccess(actor, chat)) return ForbiddenTenant(actor.Caller, "chatId", id);
        if (!string.IsNullOrWhiteSpace(chat.IncidentExternalId))
            return Conflict(new { code = "INCIDENT_ALREADY_REPORTED", message = "Esta conversación ya tiene una incidencia registrada.", incidentId = chat.IncidentExternalId });

        var residentMessages = chat.Messages
            .Where(m => SenderRoles.FromAuthorRole(m.AuthorRole) == Sender.Resident)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => m.Body.Trim())
            .Where(b => b.Length > 0)
            .ToList();
        var description = (resource?.Description ?? string.Empty).Trim();
        if (description.Length == 0) description = string.Join("\n", residentMessages.TakeLast(3));
        if (description.Length == 0)
            return BadRequest(new { code = "DESCRIPTION_REQUIRED", message = "Describe el problema antes de reportarlo." });
        var title = (resource?.Title ?? string.Empty).Trim();
        if (title.Length == 0) title = !string.IsNullOrWhiteSpace(chat.Topic) ? chat.Topic.Trim() : description.Split('\n')[0].Trim();
        if (title.Length > 80) title = title[..80].TrimEnd() + "…";

        var created = await incidentCommands.Handle(
            new CreateIncidentCommand(
                $"incident-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                actor.TenantId,
                description,
                IncidentStatuses.Open,
                actor.ExternalId,
                actor.Name,
                null,
                null,
                title,
                Actor: new IncidentActor(actor.ExternalId, actor.Name, "resident"),
                SourceChatId: chat.ExternalId),
            ct);
        if (created.IsFailure)
            return BadRequest(new { code = "INCIDENT_NOT_CREATED", message = created.Message });

        var incident = created.Value!;
        if (incidentTriage.IsAvailableFor(actor.Caller.TenantAdmin))
            await incidentTriage.SuggestAsync(incident.ExternalId, actor.Caller.TenantAdmin, ct, TimeSpan.FromSeconds(8));

        chat.LinkIncident(incident.ExternalId);
        chat.AddMessage(Sender.Assistant,
            $"Registré tu reporte como la incidencia «{incident.Title}». La administración ya la ve en su bandeja; el seguimiento continúa en Incidencias y aquí te avisaré cuando se resuelva.");
        // Registrada la incidencia, la conversación queda cerrada (solo lectura).
        chat.Close();
        await db.SaveChangesAsync(ct);

        try
        {
            await hub.Clients.Group(SupportHub.OwnerGroup(actor.TenantId))
                .SendAsync("incidentCreated", new { id = incident.ExternalId, title = incident.Title }, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR incidentCreated skipped");
        }

        return await RespondAsync(chat, ct);
    }

    [HttpPost("chats/{id}/messages")]
    public async Task<IActionResult> PostMessage(
        string id,
        [FromBody] PostSupportMessageResource resource,
        CancellationToken ct)
    {
        var (actor, error) = await ResolveActorAsync(ct);
        if (actor is null) return error!;

        var body = (resource.Body ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(body))
            return BadRequest(new { code = "EMPTY_BODY", message = "El mensaje es obligatorio." });
        if (body.Length > MaxMessageLength)
            return BadRequest(new { code = "MESSAGE_TOO_LONG", message = $"El mensaje no puede superar {MaxMessageLength} caracteres." });

        var chat = await FindChatAsync(id, ct);
        if (chat is null) return NotFound(new { code = "CHAT_NOT_FOUND" });
        if (!CanAccess(actor, chat)) return ForbiddenTenant(actor.Caller, "chatId", id);

        // Conversación cerrada (el residente confirmó que se resolvió o se registró como incidencia): solo lectura.
        if (chat.Status == ConversationStatus.Closed)
            return Conflict(new
            {
                code = "CHAT_CLOSED",
                message = "Esta conversación está cerrada. Inicia una nueva para otra consulta.",
            });

        var locale = string.IsNullOrWhiteSpace(resource.Locale) ? "es" : resource.Locale.Trim();

        if (chat.Kind == SupportChat.KindAdmin)
            return await HandleAdminThreadMessageAsync(actor, chat, body, ct);

        return await HandleAiThreadMessageAsync(actor, chat, body, locale, ct);
    }

    private async Task<IActionResult> HandleAdminThreadMessageAsync(
        Actor actor,
        SupportChat chat,
        string body,
        CancellationToken ct)
    {
        if (actor.IsAdmin)
        {
            chat.AddMessage(Sender.Admin, body, actor.Name);
            chat.MarkAnswered(); // US032: responder marca el chat como "Atendido".
        }
        else
        {
            if (!string.Equals(chat.ResidentId, actor.ExternalId, StringComparison.Ordinal))
                return ForbiddenTenant(actor.Caller, "chatId", chat.ExternalId);
            chat.AddMessage(Sender.Resident, body, actor.Name);
            chat.MarkPending();
        }

        await db.SaveChangesAsync(ct);
        return await RespondAsync(chat, ct);
    }

    /// <summary>
    /// Turno en el chat con el asistente. El mensaje del residente solo se guarda si hubo respuesta;
    /// ante una falla del proveedor se responde 503 y se puede reintentar sin duplicar.
    /// </summary>
    private async Task<IActionResult> HandleAiThreadMessageAsync(
        Actor actor,
        SupportChat chat,
        string body,
        string locale,
        CancellationToken ct)
    {
        // TS010: defensa en profundidad (además de CanAccess): solo el residente dueño habla con el asistente.
        if (actor.IsAdmin || !string.Equals(chat.ResidentId, actor.ExternalId, StringComparison.Ordinal))
            return ForbiddenTenant(actor.Caller, "chatId", chat.ExternalId);

        var askedAt = DateTimeOffset.UtcNow;

        // Ya derivado: la conversación sigue con la administración (sin IA).
        if (!string.IsNullOrWhiteSpace(chat.LinkedChatId))
        {
            AddResidentMessage(chat, actor, body, askedAt);
            var adminChat = await FindChatAsync(chat.LinkedChatId, ct);
            if (adminChat is not null && adminChat.Kind == SupportChat.KindAdmin && adminChat.OwnerAdminId == chat.OwnerAdminId)
            {
                adminChat.AddMessage(Sender.Resident, body, actor.Name);
                adminChat.MarkPending();
            }

            await db.SaveChangesAsync(ct);
            return await RespondAsync(chat, ct);
        }

        // US044: si el residente pide a una persona, se deriva sin llamar a la IA.
        if (SupportEscalate.ResidentWantsAdmin(body))
        {
            AddResidentMessage(chat, actor, body, askedAt);
            Escalate(chat, EscalationNotice(locale));
            await db.SaveChangesAsync(ct);
            return await RespondAsync(chat, ct);
        }

        // US044: las emergencias no las resuelve la IA; se orienta y se deriva.
        if (AnswerPolicy.IsEmergency(body))
        {
            AddResidentMessage(chat, actor, body, askedAt);
            chat.AddMessage(Sender.Assistant, AnswerPolicy.EmergencyMessage(locale));
            Escalate(chat, notice: null);
            await db.SaveChangesAsync(ct);
            return await RespondAsync(chat, ct);
        }

        // US045: saludos y temas ajenos se responden sin IA, sin gastar el tope ni derivar.
        var scope = QuestionRelevancePolicy.Classify(body);
        if (scope != QuestionScope.Building)
        {
            var fixedReply = scope switch
            {
                QuestionScope.SmallTalk => QuestionRelevancePolicy.SmallTalkMessage(locale, body),
                QuestionScope.OffTopic => QuestionRelevancePolicy.OffTopicMessage(locale),
                _ => QuestionRelevancePolicy.UnclearMessage(locale),
            };
            AddResidentMessage(chat, actor, body, askedAt);
            chat.AddMessage(Sender.Assistant, fixedReply);
            chat.MarkAnswered();
            await db.SaveChangesAsync(ct);
            await aiQuota.RecordFilteredAsync(actor.Caller.BillingAdmin.ExternalId, ct);
            return await RespondAsync(chat, ct);
        }

        // TS010: tope de llamadas a Gemini por residente; el mensaje no se guarda, así el reintento no duplica.
        if (!rateLimiter.TryAcquire(actor.ExternalId))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                code = "GEMINI_RATE_LIMITED",
                message = "Demasiadas solicitudes al asistente. Espera un momento.",
            });
        }

        // US041 / CON11: tope mensual del plan, contado para toda la cuenta (en Portafolio, la matriz).
        var cap = planQuota.MonthlyAiQueryCap(
            actor.Caller.TenantAdmin.SubscriptionPlanId,
            actor.Caller.TenantAdmin.SubscriptionPaidUntil);
        var reservation = await aiQuota.TryConsumeAsync(actor.Caller.BillingAdmin.ExternalId, cap, ct);
        if (reservation is null)
        {
            // US044: con el tope alcanzado la consulta pasa a la administración; el chat derivado va en el body.
            AddResidentMessage(chat, actor, body, askedAt);
            chat.AddMessage(Sender.Assistant, AnswerPolicy.MonthlyCapReachedMessage(locale));
            Escalate(chat, notice: null);
            await db.SaveChangesAsync(ct);

            var capPayload = ToResource(chat);
            await NotifyAsync(chat, capPayload, ct);
            await NotifyLinkedAsync(chat, ct);
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                code = "AI_MONTHLY_CAP_REACHED",
                message = AnswerPolicy.MonthlyCapReachedMessage(locale),
                derived = true,
                cap,
                chat = capPayload,
            });
        }

        // US045: límite diario por residente; si se alcanzó, se devuelve la reserva mensual.
        var dailyReservation = await aiQuota.TryConsumeResidentDailyAsync(
            chat.OwnerAdminId, actor.ExternalId, AnswerPolicy.ResidentDailyAiQueries, ct);
        if (dailyReservation is null)
        {
            await aiQuota.ReleaseAsync(reservation, ct);
            AddResidentMessage(chat, actor, body, askedAt);
            chat.AddMessage(Sender.Assistant, AnswerPolicy.ResidentDailyLimitMessage(locale, AnswerPolicy.ResidentDailyAiQueries));
            chat.MarkAnswered();
            await db.SaveChangesAsync(ct);
            return await RespondAsync(chat, ct);
        }

        // Si la IA no llega a responder, la consulta reservada se devuelve.
        var consumed = false;
        try
        {
            var retrievalQuery = BuildRetrievalQuery(chat, body);
            IReadOnlyList<KnowledgeSource> docSources;
            try
            {
                docSources = await contextIndexer.RetrieveAsync(chat.OwnerAdminId, retrievalQuery, topK: 5, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "RAG retrieve failed");
                docSources = [];
            }

            var sources = docSources.Concat(AppUsageGuide.Match(retrievalQuery, locale)).ToList();

            // US044: sin base documental se deriva sin llamar a la IA (no se descuenta del tope).
            if (AnswerPolicy.MustDerive(sources))
            {
                await aiQuota.ReleaseAsync(reservation, ct);
                await aiQuota.ReleaseResidentDailyAsync(dailyReservation, ct);
                consumed = true;
                AddResidentMessage(chat, actor, body, askedAt);
                chat.AddMessage(Sender.Assistant, AnswerPolicy.NoDocumentaryBasisMessage(locale));
                Escalate(chat, notice: null);
                await db.SaveChangesAsync(ct);
                return await RespondAsync(chat, ct);
            }

            var history = BuildHistory(chat, body, locale);
            var prompt = ResidentAssistantPrompt.Build(locale, actor.Name, AnswerPolicy.BuildContext(sources));

            string reply;
            try
            {
                reply = await gemini.GenerateReplyAsync(history, prompt, ct);
            }
            catch (GeminiException ex)
            {
                await aiQuota.ReleaseAsync(reservation, ct);
                await aiQuota.ReleaseResidentDailyAsync(dailyReservation, ct);
                consumed = true;
                logger.LogWarning(ex, "Gemini unavailable ({Code}) for chat {ChatId}", ex.Code, chat.ExternalId);
                return AssistantUnavailable(ex, locale);
            }

            consumed = true;
            AddResidentMessage(chat, actor, body, askedAt);
            var answer = AnswerPolicy.ToAnswer(reply, sources);
            var hasText = !string.IsNullOrWhiteSpace(answer.Text);
            var assistantText = hasText ? answer.Text : AnswerPolicy.NoDocumentaryBasisMessage(locale);
            chat.AddMessage(
                SenderRoles.Assistant,
                assistantText,
                citedDocumentId: hasText && answer.Grounded ? answer.SourceDocumentId : null,
                citedSource: hasText && answer.Grounded ? answer.SourceName : null);
            if (answer.MustDerive)
                Escalate(chat, notice: null);
            else
                chat.MarkAnswered();
            await TrySummarizeTitleAsync(chat, actor, locale, ct);
            await db.SaveChangesAsync(ct);
            return await RespondAsync(chat, ct);
        }
        catch
        {
            if (!consumed)
            {
                await aiQuota.ReleaseAsync(reservation, CancellationToken.None);
                await aiQuota.ReleaseResidentDailyAsync(dailyReservation, CancellationToken.None);
            }

            throw;
        }
    }

    private static string BuildRetrievalQuery(SupportChat chat, string body)
    {
        var previous = chat.Messages
            .Where(m => SenderRoles.FromAuthorRole(m.AuthorRole) == Sender.Resident)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Select(m => m.Body.Trim())
            .FirstOrDefault(b => b.Length > 0);

        return string.IsNullOrEmpty(previous) ? body : $"{previous}\n{body}";
    }

    /// <summary>TS008 / US043: solo los mensajes de esta conversación (del mismo residente), últimos N turnos con su cita.</summary>
    private static List<GeminiTurn> BuildHistory(SupportChat chat, string body, string locale)
    {
        var turns = chat.Messages
            .Select(m => new { Message = m, Sender = SenderRoles.FromAuthorRole(m.AuthorRole) })
            .Where(x => x.Sender is Sender.Resident or Sender.Assistant)
            .OrderBy(x => x.Message.CreatedAt)
            .ThenBy(x => x.Message.Id)
            .TakeLast(MaxHistoryTurns)
            .Select(x => new GeminiTurn(
                x.Sender == Sender.Assistant ? SenderRoles.Assistant : SenderRoles.ResidentInAiChat,
                x.Sender == Sender.Assistant
                    ? AnswerPolicy.WithCitation(x.Message.Body, x.Message.CitedSource, locale)
                    : x.Message.Body))
            .ToList();

        turns.Add(new GeminiTurn(SenderRoles.ResidentInAiChat, body));
        return turns;
    }

    private IActionResult AssistantUnavailable(GeminiException ex, string locale)
    {
        var isEnglish = locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var missingKey = ex.Code == GeminiException.ApiKeyMissing;
        var code = missingKey ? GeminiException.ApiKeyMissing : "AI_UNAVAILABLE";
        var reason = ex.Code switch
        {
            GeminiException.Timeout => "timeout",
            GeminiException.Busy => "busy",
            GeminiException.Unavailable => "unavailable",
            GeminiException.ApiKeyMissing => "not_configured",
            _ => "error",
        };
        var retryable = !missingKey;
        var message = missingKey
            ? (isEnglish
                ? "The assistant is not configured right now. Your message was not sent; you can ask to speak with administration."
                : "El asistente no está configurado en este momento. Tu mensaje no se envió; puedes pedir hablar con administración.")
            : (isEnglish
                ? "The assistant is unavailable right now. Your message was not sent; please try again in a few seconds."
                : "El asistente no está disponible en este momento. Tu mensaje no se envió; vuelve a intentarlo en unos segundos.");

        Response.Headers.RetryAfter = "5";
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            code,
            reason,
            retryable,
            message,
        });
    }

    private async Task TrySummarizeTitleAsync(SupportChat chat, Actor actor, string locale, CancellationToken ct)
    {
        if (chat.TitleSummarized) return;

        var dialogueCount = chat.Messages.Count(m =>
            SenderRoles.FromAuthorRole(m.AuthorRole) is Sender.Resident or Sender.Assistant);
        if (dialogueCount < 2) return;

        var turns = chat.Messages
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new GeminiTurn(m.AuthorRole, m.Body))
            .ToList();

        // Accesorio: no debe alargar el turno del residente; sus fallos no cuentan para el breaker.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(TitleSummaryBudget);
        try
        {
            // Clave aparte: el título no gasta el cupo de las respuestas al residente.
            if (!rateLimiter.TryAcquire(actor.ExternalId + ":title")) return;

            var title = await gemini.SummarizeTitleAsync(turns, locale, budgetCts.Token);
            if (!string.IsNullOrWhiteSpace(title))
                chat.SetTopic(title, summarized: true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Title summarize skipped");
        }
    }

    private static void AddResidentMessage(SupportChat chat, Actor actor, string body, DateTimeOffset askedAt)
    {
        SupportMessage.Create(
            chat,
            SenderRoles.ToAuthorRole(Sender.Resident, chat.Kind),
            body,
            actor.Name,
            createdAt: askedAt);
        if (!chat.TitleSummarized && chat.Topic is "Nuevo chat" or "New chat" or "Soporte")
            chat.SetTopic(TruncateTitle(body));
        chat.MarkPending();
    }

    private static string EscalationNotice(string locale) =>
        locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "I passed you to the board / building admin. They can reply in this same chat."
            : "Te pasé con la junta / administración. Pueden responderte aquí mismo.";

    /// <summary>Deriva: crea la fila kind=admin (pendiente) con el transcript y marca el chat IA como derivado.</summary>
    private void Escalate(SupportChat aiChat, string? notice)
    {
        if (!string.IsNullOrWhiteSpace(aiChat.LinkedChatId))
            return;

        if (!string.IsNullOrWhiteSpace(notice))
            aiChat.AddMessage(Sender.Assistant, notice);

        var adminId = $"adminchat-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Random.Shared.Next(1000, 9999)}";
        var adminChat = SupportChat.CreateAdmin(
            adminId,
            aiChat.OwnerAdminId,
            aiChat.ResidentId,
            aiChat.ResidentName,
            aiChat.Topic,
            SupportChat.SourceAiEscalation,
            aiChat.ExternalId);

        // Copia transcript a la fila admin (mensajes propios, no blob compartido).
        foreach (var m in aiChat.Messages.OrderBy(x => x.CreatedAt).ToList())
            adminChat.CopyMessage(m);

        aiChat.Derive(adminId);
        db.Set<SupportChat>().Add(adminChat);
    }

    private async Task<IActionResult> RespondAsync(SupportChat chat, CancellationToken ct)
    {
        var payload = ToResource(chat);
        await NotifyAsync(chat, payload, ct);
        await NotifyLinkedAsync(chat, ct);
        return Ok(payload);
    }

    private async Task NotifyLinkedAsync(SupportChat chat, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chat.LinkedChatId)) return;
        var linked = await FindChatAsync(chat.LinkedChatId, ct);
        if (linked is not null && linked.OwnerAdminId == chat.OwnerAdminId)
            await NotifyAsync(linked, ToResource(linked), ct);
    }

    private async Task NotifyAsync(SupportChat chat, object payload, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(SupportHub.OwnerGroup(chat.OwnerAdminId))
                .SendAsync("chatUpdated", payload, ct);
            await hub.Clients.Group(SupportHub.ChatGroup(chat.ExternalId))
                .SendAsync("chatUpdated", payload, ct);
            if (!string.IsNullOrWhiteSpace(chat.ResidentId))
                await hub.Clients.Group(SupportHub.ResidentGroup(chat.OwnerAdminId, chat.ResidentId))
                    .SendAsync("chatUpdated", payload, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR notify skipped");
        }
    }

    private async Task<SupportChat?> FindChatAsync(string id, CancellationToken ct)
    {
        var clean = (id ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(clean)) return null;
        return await db.Set<SupportChat>()
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
            .FirstOrDefaultAsync(c => c.ExternalId == clean || c.Id.ToString() == clean, ct);
    }

    private static bool CanAccess(Actor actor, SupportChat chat)
    {
        if (!string.Equals(chat.OwnerAdminId, actor.TenantId, StringComparison.Ordinal))
            return false;
        return actor.IsAdmin || string.Equals(chat.ResidentId, actor.ExternalId, StringComparison.Ordinal);
    }

    private async Task<(Actor? Actor, IActionResult? Error)> ResolveActorAsync(CancellationToken ct)
    {
        var (caller, error) = await ResolveTenantAsync(null, ct);
        if (caller is null) return (null, error);
        return (new Actor(caller), null);
    }

    private static object ToResource(SupportChat chat, IEnumerable<SupportMessage>? onlyMessages = null)
    {
        var source = (onlyMessages ?? chat.Messages).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id);
        var messages = source.Select(m => new
        {
            id = m.ExternalId,
            role = NormalizeRoleForClient(m.AuthorRole, chat.Kind),
            authorRole = NormalizeAuthorRole(m.AuthorRole),
            body = m.Body,
            authorName = m.AuthorName,
            citedDocumentId = m.CitedDocumentId,
            citedSource = m.CitedSource,
            createdAt = m.CreatedAt.ToString("o"),
        });

        return new
        {
            id = chat.ExternalId,
            ownerAdminId = chat.OwnerAdminId,
            residentId = chat.ResidentId,
            residentName = chat.ResidentName,
            topic = chat.Topic,
            title = chat.Topic,
            titleSummarized = chat.TitleSummarized,
            status = string.IsNullOrWhiteSpace(chat.Status) ? ConversationStatus.Open : chat.Status,
            pending = ConversationStatus.IsPending(chat.Status),
            kind = chat.Kind,
            source = chat.Source,
            linkedChatId = chat.LinkedChatId ?? string.Empty,
            incidentId = chat.IncidentExternalId ?? string.Empty,
            escalatedChatId = chat.Kind == SupportChat.KindAi ? (chat.LinkedChatId ?? string.Empty) : string.Empty,
            aiChatId = chat.Kind == SupportChat.KindAdmin ? (chat.LinkedChatId ?? string.Empty) : string.Empty,
            messages,
            closedAt = chat.ClosedAt?.ToString("o"),
            updatedAt = chat.UpdatedAt.ToString("o"),
            createdAt = chat.CreatedAt.ToString("o"),
        };
    }

    private static string NormalizeRoleForClient(string role, string kind) =>
        role switch
        {
            "assistant" => "ai",
            "resident" => kind == SupportChat.KindAi ? "user" : "resident",
            _ => role,
        };

    private static string NormalizeAuthorRole(string role) =>
        role switch
        {
            "assistant" or "ai" => "assistant",
            "user" => "resident",
            _ => role,
        };

    private static string TruncateTitle(string text)
    {
        var clean = text.Trim().Replace('\n', ' ');
        return clean.Length <= 38 ? clean : clean[..38] + "…";
    }

    private sealed record Actor(CallerContext Caller)
    {
        public string ExternalId => Caller.UserExternalId;
        public string Name => Caller.User.Name;
        public bool IsAdmin => Caller.IsAdmin;
        /// <summary>ownerAdminId externo del edificio (siempre del token).</summary>
        public string TenantId => Caller.TenantExternalId;
    }
}

public class ReportIncidentFromChatResource
{
    public string? Title { get; set; }
    public string? Description { get; set; }
}

public class CreateSupportChatResource
{
    public string? Kind { get; set; }
    public string? Topic { get; set; }
}

public class PostSupportMessageResource
{
    public string? Body { get; set; }
    public string? Locale { get; set; }
}

public class UpdateSupportChatStatusResource
{
    public string? Status { get; set; }
}
