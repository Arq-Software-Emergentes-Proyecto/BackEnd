using BillingApp = BuildingFex.Api.Billing.Application.Internal;
using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Repositories;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Shared.Domain.Repositories;
using MercadoPago.Client.Payment;
using MercadoPago.Client.Preference;
using MercadoPago.Config;
using MercadoPago.Resource.Payment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BuildingFex.Api.Finances.Application.Internal.MercadoPago;

public class MercadoPagoService(
    IOptions<MercadoPagoSettings> settings,
    IReceiptRepository receiptRepository,
    IFeeRepository feeRepository,
    IPaymentRepository paymentRepository,
    IMaintenanceCheckoutRepository checkoutRepository,
    IUserRepository userRepository,
    FinanceOwnerResolver ownerResolver,
    IUnitOfWork unitOfWork,
    IHostEnvironment hostEnvironment,
    ILogger<MercadoPagoService> logger,
    BuildingFex.Api.Billing.Application.Internal.ISubscriptionService subscriptionService) : IMercadoPagoService
{
    private readonly MercadoPagoSettings _settings = settings.Value;

    /// <summary>
    /// SUBSCRIPTION:{admin}:{plan}[:{departamentos}[:{checkoutId}]] — Portafolio fija en la referencia los departamentos
    /// cobrados; el checkoutId apunta al snapshot (subscription_checkouts) con el monto fijado al crear la preferencia,
    /// así confirm y webhook verifican ese monto aunque el padrón o la lista de precios cambien entre medio.
    /// Sin Portafolio el segmento de departamentos va en 0.
    /// </summary>
    public static string BuildSubscriptionExternalReference(
        string adminExternalId,
        string planId,
        int? departmentCount = null,
        string? checkoutId = null)
    {
        var normalized = SubscriptionPlans.Normalize(planId);
        var departments = normalized == SubscriptionPlanIds.Portafolio && departmentCount is > 0 ? departmentCount.Value : 0;
        if (!string.IsNullOrWhiteSpace(checkoutId))
            return $"SUBSCRIPTION:{adminExternalId}:{normalized}:{departments}:{checkoutId.Trim()}";
        return departments > 0
            ? $"SUBSCRIPTION:{adminExternalId}:{normalized}:{departments}"
            : $"SUBSCRIPTION:{adminExternalId}:{normalized}";
    }

    public sealed record SubscriptionReference(string AdminExternalId, string PlanId, int? DepartmentCount, string? CheckoutId = null);

    /// <summary>Parsea SUBSCRIPTION:{admin}:{plan}[:{departamentos}[:{checkoutId}]] (prefijo case-insensitive; el FE usa "subscription:").</summary>
    public static SubscriptionReference? ParseSubscriptionReference(string? externalReference)
    {
        if (string.IsNullOrWhiteSpace(externalReference)) return null;
        var parts = externalReference.Trim().Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !string.Equals(parts[0], "SUBSCRIPTION", StringComparison.OrdinalIgnoreCase))
            return null;
        int? departments = parts.Length >= 4 && int.TryParse(parts[3], out var n) && n > 0 ? n : null;
        var checkoutId = parts.Length >= 5 && !string.IsNullOrWhiteSpace(parts[4]) ? parts[4].Trim() : null;
        return new SubscriptionReference(parts[1], SubscriptionPlans.Normalize(parts[2]), departments, checkoutId);
    }

    /// <summary>Monto esperado del cobro de la suscripción (Portafolio: S/4 × dptos., mínimo S/600).</summary>
    public static decimal ExpectedSubscriptionAmount(string planId, int? departmentCount) =>
        SubscriptionPlans.MonthlyPricePen(planId, departmentCount ?? 0);

    public static string BuildMaintenanceExternalReference(
        string residentExternalId,
        string ownerAdminExternalId,
        string? checkoutId = null) =>
        string.IsNullOrWhiteSpace(checkoutId)
            ? $"MAINTENANCE:{residentExternalId}:{ownerAdminExternalId}"
            : $"MAINTENANCE:{residentExternalId}:{ownerAdminExternalId}:{checkoutId}";

    private void EnsureSdkConfigured()
    {
        if (string.IsNullOrWhiteSpace(MercadoPagoConfig.AccessToken))
            MercadoPagoConfig.AccessToken = _settings.AccessToken;
    }

    private bool IsMercadoPagoConfigured() =>
        !string.IsNullOrWhiteSpace(_settings.AccessToken) &&
        !_settings.AccessToken.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidMercadoPagoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Host.Equals("127.0.0.1")) return false;
        return true;
    }

    private string ResolveFrontendBaseForCheckout(string? requestOverride)
    {
        var fromRequest = string.IsNullOrWhiteSpace(requestOverride)
            ? null
            : requestOverride.TrimEnd('/');

        if (fromRequest != null && IsValidMercadoPagoUrl(fromRequest))
            return fromRequest;

        var fromSettings = _settings.FrontendBaseUrl.TrimEnd('/');
        if (IsValidMercadoPagoUrl(fromSettings))
            return fromSettings;

        return fromRequest ?? fromSettings;
    }

    private string? ResolveNotificationUrl()
    {
        var url = _settings.NotificationUrl?.Trim();
        return IsValidMercadoPagoUrl(url) ? url : null;
    }

    private static PreferenceBackUrlsRequest BuildBackUrls(string frontendBase, string successPath)
    {
        var baseUrl = frontendBase.TrimEnd('/');
        return new PreferenceBackUrlsRequest
        {
            Success = $"{baseUrl}{successPath}",
            Failure = $"{baseUrl}{successPath.Replace("success", "failure", StringComparison.Ordinal)}",
            Pending = $"{baseUrl}{successPath.Replace("success", "pending", StringComparison.Ordinal)}",
        };
    }

    private async Task<PreferenceResult> CreatePreferenceSafeAsync(
        PreferenceRequest request, string logContext, CancellationToken ct)
    {
        try
        {
            var client = new PreferenceClient();
            var preference = await client.CreateAsync(request, cancellationToken: ct);
            var initPoint = preference.InitPoint ?? preference.SandboxInitPoint;
            logger.LogInformation("MercadoPago preference created: {PreferenceId} ({Context})", preference.Id, logContext);
            return new PreferenceResult(preference.Id!, initPoint);
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            logger.LogError(ex, "MercadoPago preference failed ({Context}): {Detail}", logContext, detail);
            throw new InvalidOperationException($"Mercado Pago rechazó la preferencia: {detail}", ex);
        }
    }


    private bool IsDemoAllowed() => hostEnvironment.IsDevelopment() || _settings.AllowDemoPayments;

    private static bool IsReceiptPayable(Receipt r) =>
        !string.Equals(r.Status, "Paid", StringComparison.OrdinalIgnoreCase);

    private static bool IsFeePayable(Fee f) =>
        !string.Equals(f.Status, "Pagado", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(f.Status, "Paid", StringComparison.OrdinalIgnoreCase);

    private static decimal ReceiptTotal(Receipt r) => r.Amount + r.LateFee + r.ExtraCharges;

    private static bool AmountMatches(decimal expected, decimal? paid) =>
        paid.HasValue && Math.Abs(paid.Value - expected) <= 0.01m;

    private static bool CurrencyOk(string? currencyId) =>
        string.IsNullOrWhiteSpace(currencyId) || string.Equals(currencyId, "PEN", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset ParsePaidAt(string? iso) =>
        DateTimeOffset.TryParse(iso, out var at) ? at : DateTimeOffset.UtcNow;

    public sealed record MaintenanceReference(string ResidentExternalId, string OwnerAdminExternalId, string? CheckoutId);

    /// <summary>MAINTENANCE:{resident}:{owner}[:{checkoutId}] (case-insensitive prefix; el FE usa "maintenance:").</summary>
    public static MaintenanceReference? ParseMaintenanceReference(string? externalRef)
    {
        if (string.IsNullOrWhiteSpace(externalRef)
            || !externalRef.StartsWith("MAINTENANCE:", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = externalRef.Split(':');
        if (parts.Length is < 3 or > 4) return null;
        if (string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2])) return null;

        return new MaintenanceReference(
            parts[1].Trim(),
            parts[2].Trim(),
            parts.Length == 4 && !string.IsNullOrWhiteSpace(parts[3]) ? parts[3].Trim() : null);
    }

    private sealed record ReconcileOutcome(bool Reconciled, int ItemsPaid, string Status);

    // ---------------------------------------------------------------- receipt (legacy single)

    public async Task<PreferenceResult> CreatePreferenceAsync(
        CreatePreferenceRequest request, CancellationToken ct = default)
    {
        if (!IsMercadoPagoConfigured())
            throw new InvalidOperationException("Mercado Pago no está configurado. Agrega AccessToken en appsettings.Local.json.");

        EnsureSdkConfigured();

        var owner = await ownerResolver.ResolveOwnerAdminAsync(request.OwnerAdminExternalId, ct)
            ?? throw new InvalidOperationException("Valid ownerAdminId is required.");

        var receipt = await receiptRepository.FindByExternalIdAsync(request.ReceiptExternalId, ct);
        if (receipt is null
            || receipt.OwnerAdminId != owner.Id
            || (request.ResidentExternalId is not null
                && !string.Equals(receipt.ResidentExternalId, request.ResidentExternalId, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"Receipt '{request.ReceiptExternalId}' not found.");

        if (!IsReceiptPayable(receipt))
            throw new InvalidOperationException("El recibo ya está pagado.");

        var totalAmount = ReceiptTotal(receipt);
        var frontend = ResolveFrontendBaseForCheckout(null);

        if (!IsValidMercadoPagoUrl(frontend))
            throw new InvalidOperationException(
                "MercadoPago__FrontendBaseUrl debe ser HTTPS público (ej. https://tu-app.vercel.app). localhost no es válido.");

        var preferenceRequest = new PreferenceRequest
        {
            Items =
            [
                new PreferenceItemRequest
                {
                    Title = $"BuildingFex – Recibo {receipt.ExternalId}",
                    Description = receipt.Concept ?? "Pago de expensas",
                    Quantity = 1,
                    CurrencyId = "PEN",
                    UnitPrice = totalAmount,
                }
            ],
            ExternalReference = receipt.ExternalId,
            BackUrls = BuildBackUrls(frontend, "/app/resident/finance?payment=success"),
            AutoReturn = "approved",
            NotificationUrl = ResolveNotificationUrl(),
        };

        return await CreatePreferenceSafeAsync(
            preferenceRequest, $"receipt {receipt.ExternalId}", ct);
    }

    // ---------------------------------------------------------------- maintenance checkout

    private async Task<List<MaintenanceCheckout.Item>> CollectPayableItemsAsync(
        string ownerAdminExternalId,
        string residentExternalId,
        CancellationToken ct)
    {
        var receipts = (await receiptRepository.ListAsync(ownerAdminExternalId, residentExternalId, ct))
            .Where(IsReceiptPayable)
            .Select(r => new MaintenanceCheckout.Item(MaintenanceCheckout.ItemReceipt, r.ExternalId, ReceiptTotal(r)));

        var fees = (await feeRepository.ListAsync(ownerAdminExternalId, residentExternalId, ct))
            .Where(IsFeePayable)
            .Select(f => new MaintenanceCheckout.Item(MaintenanceCheckout.ItemFee, f.ExternalId, f.Amount));

        return receipts.Concat(fees).ToList();
    }

    /// <summary>
    /// Fija (persiste) los ítems y el total ANTES de ir a Mercado Pago. La referencia externa
    /// lleva el id del checkout para que la conciliación pague solo esos ítems.
    /// </summary>
    public async Task<MaintenanceCheckoutResult> CreateMaintenanceCheckoutPreferenceAsync(
        CreateMaintenanceCheckoutRequest request,
        CancellationToken ct = default)
    {
        var owner = await ownerResolver.ResolveOwnerAdminAsync(request.OwnerAdminExternalId, ct)
            ?? throw new InvalidOperationException("Valid ownerAdminId is required.");

        var items = await CollectPayableItemsAsync(owner.ExternalId, request.ResidentExternalId, ct);
        if (items.Count == 0)
            throw new InvalidOperationException("No hay cuotas pendientes para pagar.");

        var checkout = MaintenanceCheckout.Create(owner.Id, request.ResidentExternalId, items);
        await checkoutRepository.AddAsync(checkout, ct);
        await unitOfWork.CompleteAsync(ct);

        var externalRef = BuildMaintenanceExternalReference(
            request.ResidentExternalId, owner.ExternalId, checkout.ExternalId);

        MaintenanceCheckoutResult Demo() =>
            new("DEMO", null, checkout.ExternalId, checkout.TotalAmount, externalRef);

        if (!IsMercadoPagoConfigured())
        {
            logger.LogWarning("MercadoPago not configured – maintenance checkout unavailable for demo.");
            return Demo();
        }

        EnsureSdkConfigured();

        var frontend = ResolveFrontendBaseForCheckout(request.FrontendBaseUrl);
        if (!IsValidMercadoPagoUrl(frontend))
        {
            logger.LogWarning(
                "No HTTPS frontend URL for maintenance checkout (got '{Frontend}').",
                frontend);
            return Demo();
        }

        var preferenceRequest = new PreferenceRequest
        {
            Items =
            [
                new PreferenceItemRequest
                {
                    Title = "BuildingFex – Cuotas de mantenimiento",
                    Description = $"{items.Count} cuota(s) pendiente(s)",
                    Quantity = 1,
                    CurrencyId = "PEN",
                    UnitPrice = checkout.TotalAmount,
                }
            ],
            ExternalReference = externalRef,
            Payer = string.IsNullOrWhiteSpace(request.PayerEmail)
                ? null
                : new PreferencePayerRequest { Email = request.PayerEmail },
            BackUrls = BuildBackUrls(frontend, "/app/resident/finance?payment=success"),
            AutoReturn = "approved",
            NotificationUrl = ResolveNotificationUrl(),
        };

        var preference = await CreatePreferenceSafeAsync(
            preferenceRequest,
            $"maintenance resident {request.ResidentExternalId} checkout {checkout.ExternalId} ({items.Count} items)",
            ct);

        checkout.AttachPreference(preference.PreferenceId);
        checkoutRepository.Update(checkout);
        await unitOfWork.CompleteAsync(ct);

        return new MaintenanceCheckoutResult(
            preference.PreferenceId,
            preference.InitPoint,
            checkout.ExternalId,
            checkout.TotalAmount,
            externalRef);
    }

    public async Task<MaintenancePaymentResult> ConfirmMaintenancePaymentAsync(
        ConfirmMaintenancePaymentRequest request,
        CancellationToken ct = default)
    {
        var owner = await ownerResolver.ResolveOwnerAdminAsync(request.OwnerAdminExternalId, ct)
            ?? throw new InvalidOperationException("Valid ownerAdminId is required.");

        if (request.AllowDemo)
        {
            // Antes: demo:true pagaba todo lo pendiente sin cobro, en cualquier entorno.
            if (!IsDemoAllowed())
                throw new DemoPaymentsDisabledException();

            var demoCheckout = !string.IsNullOrWhiteSpace(request.CheckoutId)
                ? await checkoutRepository.FindByExternalIdAsync(request.CheckoutId, ct)
                : await checkoutRepository.FindLatestPendingAsync(owner.Id, request.ResidentExternalId, ct);

            if (demoCheckout is null
                || demoCheckout.OwnerAdminId != owner.Id
                || !string.Equals(demoCheckout.ResidentExternalId, request.ResidentExternalId, StringComparison.Ordinal)
                || !demoCheckout.IsPending)
                throw new InvalidOperationException("No hay un checkout pendiente para confirmar.");

            var now = DateTimeOffset.UtcNow;
            var demoOutcome = await ApplyCheckoutAsync(
                demoCheckout, owner.Id, $"DEMO-{Guid.NewGuid():N}"[..21], now, ct);
            return new MaintenancePaymentResult(demoOutcome.Reconciled, demoOutcome.ItemsPaid, now.ToString("o"), demoOutcome.Status);
        }

        if (!request.PaymentId.HasValue || request.PaymentId.Value <= 0)
            throw new InvalidOperationException("paymentId is required to confirm maintenance payment.");

        EnsureSdkConfigured();

        var client = new PaymentClient();
        var mpPayment = await client.GetAsync(request.PaymentId.Value, cancellationToken: ct);

        // Rechazado / cancelado / pendiente → no se toca la deuda ni se emite recibo (RNF002/RNF008).
        if (!string.Equals(mpPayment.Status, "approved", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Payment status is {mpPayment.Status ?? "unknown"}.");

        var reference = ParseMaintenanceReference(mpPayment.ExternalReference);
        if (reference is null
            || !string.Equals(reference.ResidentExternalId, request.ResidentExternalId, StringComparison.Ordinal)
            || !string.Equals(reference.OwnerAdminExternalId, owner.ExternalId, StringComparison.Ordinal))
            throw new InvalidOperationException("Payment does not match the resident maintenance checkout.");

        var paidAt = mpPayment.DateApproved ?? DateTimeOffset.UtcNow;
        var outcome = await ReconcileMaintenanceReferenceAsync(
            reference, owner, request.PaymentId.Value.ToString(), mpPayment.TransactionAmount, mpPayment.CurrencyId, paidAt, ct);

        if (outcome.Status is "amount_mismatch" or "checkout_not_found" or "duplicate_payment_review")
            throw new InvalidOperationException(outcome.Status switch
            {
                "amount_mismatch" => "El monto pagado no coincide con las cuotas del checkout.",
                "duplicate_payment_review" => "Este checkout ya fue pagado; el pago quedó para revisión del administrador.",
                _ => "Checkout no encontrado.",
            });

        return new MaintenancePaymentResult(outcome.Reconciled, outcome.ItemsPaid, paidAt.ToString("o"), outcome.Status);
    }

    /// <summary>
    /// Conciliación idempotente de un pago MP aprobado (webhook o confirm):
    /// 1) si el id de pago MP ya está registrado → already_processed (QAS03);
    /// 2) con checkoutId → solo los ítems fijados y el monto debe coincidir;
    /// 3) referencia legacy (sin checkoutId) → solo si el monto coincide EXACTAMENTE con lo pendiente.
    /// </summary>
    private async Task<ReconcileOutcome> ReconcileMaintenanceReferenceAsync(
        MaintenanceReference reference,
        BuildingFex.Api.Iam.Domain.Model.Aggregates.User owner,
        string mpPaymentId,
        decimal? transactionAmount,
        string? currencyId,
        DateTimeOffset paidAt,
        CancellationToken ct)
    {
        var already = await checkoutRepository.FindByMpPaymentIdAsync(mpPaymentId, ct);
        if (already is not null)
            return new ReconcileOutcome(true, already.GetItems().Count, "already_processed");

        if (!CurrencyOk(currencyId))
        {
            logger.LogWarning("MP payment {MpId}: unexpected currency {Currency}.", mpPaymentId, currencyId);
            return new ReconcileOutcome(false, 0, "amount_mismatch");
        }

        MaintenanceCheckout? checkout;
        if (reference.CheckoutId is not null)
        {
            checkout = await checkoutRepository.FindByExternalIdAsync(reference.CheckoutId, ct);
            if (checkout is null
                || checkout.OwnerAdminId != owner.Id
                || !string.Equals(checkout.ResidentExternalId, reference.ResidentExternalId, StringComparison.Ordinal))
            {
                logger.LogWarning("MP payment {MpId}: checkout {Checkout} not found for resident {Resident}.",
                    mpPaymentId, reference.CheckoutId, reference.ResidentExternalId);
                return new ReconcileOutcome(false, 0, "checkout_not_found");
            }

            if (!checkout.IsPending)
            {
                logger.LogWarning(
                    "MP payment {MpId}: checkout {Checkout} already paid by MP {Previous} — duplicate charge needs manual review.",
                    mpPaymentId, checkout.ExternalId, checkout.MpPaymentId);
                return new ReconcileOutcome(false, 0, "duplicate_payment_review");
            }
        }
        else
        {
            var items = await CollectPayableItemsAsync(owner.ExternalId, reference.ResidentExternalId, ct);
            if (items.Count == 0)
                return new ReconcileOutcome(false, 0, "nothing_to_reconcile");

            checkout = MaintenanceCheckout.Create(owner.Id, reference.ResidentExternalId, items);
            if (!checkout.AmountMatches(transactionAmount ?? -1))
            {
                logger.LogWarning(
                    "MP payment {MpId} (legacy ref): amount {Paid} != pending {Expected} for resident {Resident}; not reconciled.",
                    mpPaymentId, transactionAmount, checkout.TotalAmount, reference.ResidentExternalId);
                return new ReconcileOutcome(false, 0, "amount_mismatch");
            }

            await checkoutRepository.AddAsync(checkout, ct);
        }

        if (!checkout.AmountMatches(transactionAmount ?? -1))
        {
            logger.LogWarning(
                "MP payment {MpId}: amount {Paid} != checkout {Checkout} total {Expected}; not reconciled.",
                mpPaymentId, transactionAmount, checkout.ExternalId, checkout.TotalAmount);
            return new ReconcileOutcome(false, 0, "amount_mismatch");
        }

        return await ApplyCheckoutAsync(checkout, owner.Id, mpPaymentId, paidAt, ct);
    }

    /// <summary>Marca pagados SOLO los ítems del snapshot y guarda el marcador (mp_payment_id único) en la misma transacción.</summary>
    private async Task<ReconcileOutcome> ApplyCheckoutAsync(
        MaintenanceCheckout checkout,
        int ownerAdminId,
        string mpPaymentId,
        DateTimeOffset paidAt,
        CancellationToken ct)
    {
        var paidAtIso = paidAt.ToString("o");
        var itemsPaid = 0;
        var skipped = 0;

        foreach (var item in checkout.GetItems())
        {
            if (item.Type == MaintenanceCheckout.ItemReceipt)
            {
                var receipt = await receiptRepository.FindByExternalIdAsync(item.ExternalId, ct);
                if (receipt is null
                    || receipt.OwnerAdminId != ownerAdminId
                    || !string.Equals(receipt.ResidentExternalId, checkout.ResidentExternalId, StringComparison.Ordinal)
                    || !IsReceiptPayable(receipt))
                {
                    skipped++;
                    continue;
                }

                receipt.MarkAsPaid();
                receiptRepository.Update(receipt);
                await paymentRepository.AddAsync(
                    Domain.Model.Aggregates.Payment.Create(
                        $"MP-{mpPaymentId}-R-{receipt.ExternalId}",
                        ownerAdminId,
                        checkout.ResidentExternalId,
                        item.Amount,
                        feeExternalId: null,
                        feeMonth: receipt.EffectiveBillingMonth(),
                        paidAt: paidAtIso,
                        method: "Mercado Pago",
                        reference: $"MP-{mpPaymentId}"),
                    ct);
            }
            else
            {
                var fee = await feeRepository.FindByExternalIdAsync(item.ExternalId, ct);
                if (fee is null
                    || fee.OwnerAdminId != ownerAdminId
                    || !string.Equals(fee.ResidentExternalId, checkout.ResidentExternalId, StringComparison.Ordinal)
                    || !IsFeePayable(fee))
                {
                    skipped++;
                    continue;
                }

                fee.UpdateStatus("Pagado");
                feeRepository.Update(fee);
                await paymentRepository.AddAsync(
                    Domain.Model.Aggregates.Payment.Create(
                        $"MP-{mpPaymentId}-F-{fee.ExternalId}",
                        ownerAdminId,
                        checkout.ResidentExternalId,
                        item.Amount,
                        feeExternalId: fee.ExternalId,
                        feeMonth: fee.Month,
                        paidAt: paidAtIso,
                        method: "Mercado Pago",
                        reference: $"MP-{mpPaymentId}"),
                    ct);
            }

            itemsPaid++;
        }

        if (skipped > 0)
            logger.LogWarning(
                "Checkout {Checkout} (MP {MpId}): {Skipped} item(s) were no longer payable — flagged for review.",
                checkout.ExternalId, mpPaymentId, skipped);

        // Tracked (cargado) o recién agregado (legacy): el change tracker lo persiste; no llamar Update().
        checkout.MarkPaid(mpPaymentId, paidAt, needsReview: skipped > 0);

        try
        {
            await unitOfWork.CompleteAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Webhook y confirm concurrentes: el índice único (mp_payment_id / payments.external_id) gana una sola vez.
            logger.LogInformation(ex, "Checkout {Checkout}: concurrent reconciliation for MP {MpId} ignored.",
                checkout.ExternalId, mpPaymentId);
            return new ReconcileOutcome(true, 0, "already_processed");
        }

        logger.LogInformation("Checkout {Checkout} reconciled with MP {MpId}: {Count} item(s).",
            checkout.ExternalId, mpPaymentId, itemsPaid);
        return new ReconcileOutcome(true, itemsPaid, skipped > 0 ? "approved_review" : "approved");
    }

    // ---------------------------------------------------------------- subscription

    public async Task<PreferenceResult> CreateSubscriptionPreferenceAsync(
        CreateSubscriptionPreferenceRequest request,
        CancellationToken ct = default)
    {
        var planId = SubscriptionPlans.Normalize(request.PlanId);
        if (!SubscriptionPlans.IsPaid(planId))
            throw new InvalidOperationException("Only paid plans require checkout.");

        var departments = planId == SubscriptionPlanIds.Portafolio ? request.DepartmentCount : null;
        var amount = ExpectedSubscriptionAmount(planId, departments);
        var admin = await userRepository.FindByExternalIdAsync(request.AdminExternalId, ct)
            ?? throw new KeyNotFoundException($"Admin '{request.AdminExternalId}' not found.");

        if (!IsMercadoPagoConfigured())
        {
            logger.LogWarning("MercadoPago not configured – subscription checkout unavailable for demo.");
            return new PreferenceResult("DEMO", null);
        }

        EnsureSdkConfigured();

        var frontend = ResolveFrontendBaseForCheckout(request.FrontendBaseUrl);
        if (!IsValidMercadoPagoUrl(frontend))
        {
            logger.LogWarning(
                "No HTTPS frontend URL for subscription checkout (got '{Frontend}'). " +
                "Set MercadoPago__FrontendBaseUrl=https://tu-app.vercel.app in Railway.",
                frontend);
            return new PreferenceResult("DEMO", null);
        }

        // Snapshot del monto: el pago se verifica contra este checkout, no contra la lista de precios del momento del pago.
        var checkout = await subscriptionService.CreateCheckoutAsync(admin, planId, departments, amount, ct);
        var externalRef = BuildSubscriptionExternalReference(admin.ExternalId, planId, departments, checkout.ExternalId);
        var planLabel = char.ToUpper(planId[0]) + planId[1..];

        var preferenceRequest = new PreferenceRequest
        {
            Items =
            [
                new PreferenceItemRequest
                {
                    Title = $"BuildingFex – Plan {planLabel}",
                    Description = departments is > 0
                        ? $"Suscripción mensual ({planId}, {departments} dptos.)"
                        : $"Suscripción mensual ({planId})",
                    Quantity = 1,
                    CurrencyId = "PEN",
                    UnitPrice = amount,
                }
            ],
            ExternalReference = externalRef,
            Payer = string.IsNullOrWhiteSpace(request.PayerEmail)
                ? null
                : new PreferencePayerRequest { Email = request.PayerEmail },
            BackUrls = BuildBackUrls(frontend, $"/app/settings?subscription=success&plan={planId}"),
            AutoReturn = "approved",
            NotificationUrl = ResolveNotificationUrl(),
        };

        var preference = await CreatePreferenceSafeAsync(
            preferenceRequest, $"subscription admin {admin.ExternalId} plan {planId}", ct);
        return preference with { ExternalReference = externalRef };
    }

    public async Task<SubscriptionActivationResult> ConfirmSubscriptionPaymentAsync(
        string adminExternalId,
        string planId,
        long? mpPaymentId,
        bool allowDemo,
        CancellationToken ct = default)
    {
        var normalizedPlan = SubscriptionPlans.Normalize(planId);
        var admin = await userRepository.FindByExternalIdAsync(adminExternalId, ct)
            ?? throw new KeyNotFoundException($"Admin '{adminExternalId}' not found.");

        if (allowDemo)
        {
            // Antes también se activaba en producción si MP no estaba configurado (plan pago gratis).
            if (!IsDemoAllowed())
                throw new DemoPaymentsDisabledException();

            await ActivateDemoSubscriptionAsync(admin, normalizedPlan, ct);
            return new SubscriptionActivationResult(
                true,
                normalizedPlan,
                admin.SubscriptionPaidUntil?.ToString("o"));
        }

        if (!mpPaymentId.HasValue || mpPaymentId.Value <= 0)
            throw new InvalidOperationException("paymentId is required to confirm a paid subscription.");

        EnsureSdkConfigured();

        var client = new PaymentClient();
        var mpPayment = await client.GetAsync(mpPaymentId.Value, cancellationToken: ct);

        if (!string.Equals(mpPayment.Status, "approved", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Payment status is {mpPayment.Status ?? "unknown"}.");

        var reference = ParseSubscriptionReference(mpPayment.ExternalReference);
        if (reference is null
            || !string.Equals(reference.AdminExternalId, adminExternalId, StringComparison.Ordinal)
            || !string.Equals(reference.PlanId, normalizedPlan, StringComparison.Ordinal))
            throw new InvalidOperationException("Payment does not match the selected subscription plan.");

        var outcome = await ApplySubscriptionPaymentAsync(
            admin, reference, mpPayment.Id?.ToString() ?? mpPaymentId.Value.ToString(),
            mpPayment.TransactionAmount, mpPayment.CurrencyId, "confirm", ct);

        switch (outcome.Status)
        {
            case BillingApp.SubscriptionPaymentOutcome.AmountMismatch:
            case BillingApp.SubscriptionPaymentOutcome.ReviewPending:
                throw new InvalidOperationException(
                    "El monto pagado no coincide con el precio del plan. El pago quedó registrado para revisión manual.");
        }

        // applied o already_processed (el webhook llegó antes): el plan queda activo una sola vez.
        return new SubscriptionActivationResult(
            true,
            normalizedPlan,
            admin.SubscriptionPaidUntil?.ToString("o"));
    }

    /// <summary>
    /// Monto esperado: el del snapshot del checkout (si la referencia lo trae y es de este admin/plan); para
    /// referencias antiguas sin checkout, el precio vigente. Luego Billing aplica el cobro una sola vez por mp id.
    /// </summary>
    private async Task<BillingApp.SubscriptionPaymentOutcome> ApplySubscriptionPaymentAsync(
        BuildingFex.Api.Iam.Domain.Model.Aggregates.User admin,
        SubscriptionReference reference,
        string mpPaymentId,
        decimal? paidAmount,
        string? currency,
        string source,
        CancellationToken ct)
    {
        var departments = reference.DepartmentCount;
        var expected = ExpectedSubscriptionAmount(reference.PlanId, departments);
        string? checkoutId = null;
        if (!string.IsNullOrWhiteSpace(reference.CheckoutId))
        {
            var checkout = await subscriptionService.FindCheckoutAsync(reference.CheckoutId, ct);
            if (checkout is not null
                && checkout.OwnerAdminId == admin.Id
                && string.Equals(checkout.PlanId, reference.PlanId, StringComparison.Ordinal))
            {
                expected = checkout.Amount;
                departments = checkout.DepartmentCount ?? departments;
                checkoutId = checkout.ExternalId;
            }
            else
            {
                logger.LogWarning(
                    "Subscription MP {MpId}: checkout {Checkout} not found for admin {AdminId}/{PlanId}; using current price.",
                    mpPaymentId, reference.CheckoutId, admin.ExternalId, reference.PlanId);
            }
        }

        return await subscriptionService.ApplyPaymentAsync(
            admin,
            new BillingApp.SubscriptionPaymentInput(
                mpPaymentId,
                reference.PlanId,
                departments,
                paidAmount ?? 0m,
                currency,
                expected,
                checkoutId,
                source),
            ct);
    }

    // ---------------------------------------------------------------- card payment (Bricks)

    public async Task<CardPaymentResult> ProcessCardPaymentAsync(
        CardPaymentRequest request, CancellationToken ct = default)
    {
        var owner = await ownerResolver.ResolveOwnerAdminAsync(request.OwnerAdminExternalId, ct)
            ?? throw new InvalidOperationException("Valid ownerAdminId is required.");

        Receipt? receipt = null;
        if (!string.IsNullOrWhiteSpace(request.ReceiptExternalId))
        {
            receipt = await receiptRepository.FindByExternalIdAsync(request.ReceiptExternalId, ct);
            if (receipt is null
                || receipt.OwnerAdminId != owner.Id
                || (request.ResidentExternalId is not null
                    && !string.Equals(receipt.ResidentExternalId, request.ResidentExternalId, StringComparison.Ordinal)))
                throw new KeyNotFoundException($"Receipt '{request.ReceiptExternalId}' not found.");

            if (!IsReceiptPayable(receipt))
                throw new InvalidOperationException("El recibo ya está pagado.");

            // El cliente no decide cuánto cuesta el recibo.
            if (!AmountMatches(ReceiptTotal(receipt), request.TransactionAmount))
                throw new InvalidOperationException("El monto no coincide con el total del recibo.");
        }

        if (request.Token == "DEMO-TOKEN")
        {
            // Antes: marcaba el recibo como pagado en cualquier entorno.
            if (!IsDemoAllowed())
                throw new DemoPaymentsDisabledException();

            logger.LogInformation("Demo payment processed for {Email} amount {Amount}", request.PayerEmail, request.TransactionAmount);

            if (receipt != null)
            {
                await ReconcilePaymentAsync(
                    receipt, owner.Id, request.TransactionAmount,
                    $"DEMO-{Guid.NewGuid():N}"[..21],
                    DateTimeOffset.UtcNow.ToString("o"),
                    ct);
            }

            return new CardPaymentResult(
                PaymentId: $"DEMO-{Guid.NewGuid().ToString()[..8]}",
                Status: "approved",
                StatusDetail: "accredited",
                TransactionAmount: request.TransactionAmount,
                PaymentMethodId: request.PaymentMethodId,
                DateApproved: DateTimeOffset.UtcNow.ToString("o"));
        }

        if (!IsMercadoPagoConfigured())
            throw new InvalidOperationException("Mercado Pago no está configurado. Agrega AccessToken en appsettings.Local.json.");

        EnsureSdkConfigured();

        var paymentRequest = new PaymentCreateRequest
        {
            Token = request.Token,
            TransactionAmount = request.TransactionAmount,
            PaymentMethodId = request.PaymentMethodId,
            Installments = request.Installments,
            Payer = new PaymentPayerRequest { Email = request.PayerEmail },
            ExternalReference = receipt?.ExternalId ?? "GENERAL",
            Description = receipt != null ? $"BuildingFex – Recibo {receipt.ExternalId}" : "BuildingFex – Pago de expensas",
            StatementDescriptor = "BUILDINGFEX",
        };

        if (!string.IsNullOrWhiteSpace(request.IssuerId))
            paymentRequest.IssuerId = request.IssuerId;

        var client = new PaymentClient();
        var mpPayment = await client.CreateAsync(paymentRequest, cancellationToken: ct);

        logger.LogInformation(
            "MercadoPago payment processed: {MpId} status={Status} for receipt {ReceiptId}",
            mpPayment.Id, mpPayment.Status, receipt?.ExternalId ?? "GENERAL");

        // Solo un pago aprobado modifica la deuda (rechazado/cancelado/pendiente → sin cambios).
        if (string.Equals(mpPayment.Status, "approved", StringComparison.OrdinalIgnoreCase) && receipt != null)
        {
            await ReconcilePaymentAsync(
                receipt, owner.Id, mpPayment.TransactionAmount ?? request.TransactionAmount,
                mpPayment.Id?.ToString() ?? string.Empty,
                mpPayment.DateApproved?.ToString("o") ?? DateTimeOffset.UtcNow.ToString("o"),
                ct);
        }

        return new CardPaymentResult(
            PaymentId: mpPayment.Id?.ToString() ?? string.Empty,
            Status: mpPayment.Status ?? "unknown",
            StatusDetail: mpPayment.StatusDetail ?? string.Empty,
            TransactionAmount: mpPayment.TransactionAmount ?? request.TransactionAmount,
            PaymentMethodId: mpPayment.PaymentMethodId,
            DateApproved: mpPayment.DateApproved?.ToString("o"));
    }

    // ---------------------------------------------------------------- webhook

    public async Task<WebhookResult> HandleWebhookAsync(long mpPaymentId, CancellationToken ct = default)
    {
        EnsureSdkConfigured();

        var client = new PaymentClient();
        global::MercadoPago.Resource.Payment.Payment mpPayment;

        try
        {
            mpPayment = await client.GetAsync(mpPaymentId, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch MercadoPago payment {MpId}", mpPaymentId);
            return new WebhookResult(false, "fetch_error");
        }

        if (!string.Equals(mpPayment.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Webhook for MP payment {MpId}: status={Status}, skipping.", mpPaymentId, mpPayment.Status);
            return new WebhookResult(false, mpPayment.Status ?? "not_approved");
        }

        var externalRef = mpPayment.ExternalReference ?? string.Empty;
        if (externalRef.StartsWith("SUBSCRIPTION:", StringComparison.OrdinalIgnoreCase))
            return await HandleSubscriptionWebhookAsync(mpPayment, externalRef, ct);

        if (externalRef.StartsWith("MAINTENANCE:", StringComparison.OrdinalIgnoreCase))
            return await HandleMaintenanceWebhookAsync(mpPayment, externalRef, ct);

        if (string.IsNullOrWhiteSpace(externalRef))
        {
            logger.LogWarning("MercadoPago payment {MpId} has no external_reference.", mpPaymentId);
            return new WebhookResult(false, "no_external_reference");
        }

        var mpReferenceId = $"MP-{mpPaymentId}";
        var existing = await paymentRepository.FindByExternalIdAsync(mpReferenceId, ct);
        if (existing is not null)
        {
            logger.LogInformation("Webhook: payment {MpRef} already recorded, skipping.", mpReferenceId);
            return new WebhookResult(true, "already_processed");
        }

        var receipt = await receiptRepository.FindByExternalIdAsync(externalRef, ct);
        if (receipt is null)
        {
            logger.LogWarning("Webhook: receipt {ExtRef} not found for MP payment {MpId}.", externalRef, mpPaymentId);
            return new WebhookResult(false, "receipt_not_found");
        }

        if (!IsReceiptPayable(receipt))
        {
            logger.LogWarning("Webhook: receipt {ExtRef} already paid; MP payment {MpId} needs manual review.", externalRef, mpPaymentId);
            return new WebhookResult(false, "duplicate_payment_review");
        }

        if (!AmountMatches(ReceiptTotal(receipt), mpPayment.TransactionAmount) || !CurrencyOk(mpPayment.CurrencyId))
        {
            logger.LogWarning("Webhook: MP payment {MpId} amount {Paid} != receipt {ExtRef} total {Expected}.",
                mpPaymentId, mpPayment.TransactionAmount, externalRef, ReceiptTotal(receipt));
            return new WebhookResult(false, "amount_mismatch");
        }

        await ReconcilePaymentAsync(
            receipt, receipt.OwnerAdminId,
            mpPayment.TransactionAmount ?? ReceiptTotal(receipt),
            mpPaymentId.ToString(),
            mpPayment.DateApproved?.ToString("o") ?? DateTimeOffset.UtcNow.ToString("o"),
            ct);

        logger.LogInformation("Webhook: reconciled MP payment {MpId} → receipt {ReceiptId}.", mpPaymentId, externalRef);
        return new WebhookResult(true, "approved");
    }

    private async Task<WebhookResult> HandleSubscriptionWebhookAsync(
        global::MercadoPago.Resource.Payment.Payment mpPayment,
        string externalRef,
        CancellationToken ct)
    {
        var reference = ParseSubscriptionReference(externalRef);
        if (reference is null)
            return new WebhookResult(false, "invalid_subscription_reference");

        var adminExternalId = reference.AdminExternalId;
        var planId = reference.PlanId;

        var admin = await userRepository.FindByExternalIdAsync(adminExternalId, ct);
        if (admin is null)
        {
            logger.LogWarning("Webhook subscription: admin {AdminId} not found.", adminExternalId);
            return new WebhookResult(false, "admin_not_found");
        }

        var mpId = mpPayment.Id?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(mpId))
            return new WebhookResult(false, "missing_payment_id");

        var outcome = await ApplySubscriptionPaymentAsync(
            admin, reference, mpId, mpPayment.TransactionAmount, mpPayment.CurrencyId, "webhook", ct);

        switch (outcome.Status)
        {
            case BillingApp.SubscriptionPaymentOutcome.AlreadyProcessed:
                logger.LogInformation("Webhook subscription: MP {MpId} already processed, skipping.", mpId);
                return new WebhookResult(true, "already_processed");
            case BillingApp.SubscriptionPaymentOutcome.ReviewPending:
                return new WebhookResult(false, "review_pending");
            case BillingApp.SubscriptionPaymentOutcome.AmountMismatch:
                logger.LogWarning("Webhook subscription: MP {MpId} amount {Paid} does not match plan {PlanId}; saved for review.",
                    mpId, mpPayment.TransactionAmount, planId);
                return new WebhookResult(false, "amount_mismatch");
        }

        logger.LogInformation(
            "Webhook: activated subscription {PlanId} for admin {AdminId} via MP {MpId} (paid until {PaidUntil}).",
            planId, adminExternalId, mpId, outcome.PaidUntil);

        return new WebhookResult(true, "subscription_activated");
    }

    private async Task<WebhookResult> HandleMaintenanceWebhookAsync(
        global::MercadoPago.Resource.Payment.Payment mpPayment,
        string externalRef,
        CancellationToken ct)
    {
        var reference = ParseMaintenanceReference(externalRef);
        if (reference is null)
            return new WebhookResult(false, "invalid_maintenance_reference");

        var owner = await ownerResolver.ResolveOwnerAdminAsync(reference.OwnerAdminExternalId, ct);
        if (owner is null)
            return new WebhookResult(false, "owner_not_found");

        var mpId = mpPayment.Id?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(mpId))
            return new WebhookResult(false, "missing_payment_id");

        var outcome = await ReconcileMaintenanceReferenceAsync(
            reference,
            owner,
            mpId,
            mpPayment.TransactionAmount,
            mpPayment.CurrencyId,
            mpPayment.DateApproved ?? DateTimeOffset.UtcNow,
            ct);

        logger.LogInformation(
            "Webhook: maintenance MP {MpId} for resident {ResidentId}: {Status} ({Count} items).",
            mpId, reference.ResidentExternalId, outcome.Status, outcome.ItemsPaid);

        return new WebhookResult(outcome.Reconciled, outcome.Status);
    }


    /// <summary>Pago simulado (solo demo): extiende desde máx(hoy, vencimiento vigente), igual que un cobro real.</summary>
    private async Task ActivateDemoSubscriptionAsync(
        BuildingFex.Api.Iam.Domain.Model.Aggregates.User admin,
        string planId,
        CancellationToken ct)
    {
        if (!SubscriptionPlans.IsPaid(planId))
        {
            await subscriptionService.ChangeToFreeAsync(admin, ct);
            return;
        }

        var subscription = await subscriptionService.GetOrStartAsync(admin, ct);
        await subscriptionService.ActivatePaidPlanAsync(
            admin,
            planId,
            subscription.NextPaidUntil(DateTimeOffset.UtcNow),
            null,
            ct);
    }

    private async Task ReconcilePaymentAsync(
        Receipt receipt, int ownerAdminId, decimal amount,
        string mpPaymentId, string paidAt, CancellationToken ct)
    {
        var paymentExternalId = $"MP-{mpPaymentId}";

        // Idempotente: un mismo pago MP no se registra dos veces.
        if (await paymentRepository.FindByExternalIdAsync(paymentExternalId, ct) is not null)
            return;

        receipt.MarkAsPaid();
        receiptRepository.Update(receipt);

        var payment = Domain.Model.Aggregates.Payment.Create(
            paymentExternalId,
            ownerAdminId,
            receipt.ResidentExternalId,
            amount,
            feeExternalId: null,
            feeMonth: null,
            paidAt: paidAt,
            method: "Mercado Pago",
            reference: $"MP-{mpPaymentId}");

        await paymentRepository.AddAsync(payment, ct);

        try
        {
            await unitOfWork.CompleteAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Receipt {Receipt}: concurrent reconciliation for MP {MpId} ignored.",
                receipt.ExternalId, mpPaymentId);
        }
    }
}
