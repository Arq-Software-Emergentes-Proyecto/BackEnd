using System.Security.Claims;
using BuildingFex.Api.Iam.Application.Internal.Tenancy;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.ResidentCare.Interfaces.Hubs;

/// <summary>
/// Notificaciones de soporte. Los grupos se validan contra el usuario autenticado:
/// antes cualquier usuario podía unirse al grupo de otro condominio o a cualquier chat.
/// </summary>
[Authorize]
public class SupportHub(
    IUserRepository userRepository,
    AppDbContext db,
    ILogger<SupportHub> logger) : Hub
{
    public async Task JoinOwner(string ownerAdminId)
    {
        var id = (ownerAdminId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id)) return;

        var user = await ResolveUserAsync();
        if (user is not null && await ManagesTenantAsync(user, id))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, OwnerGroup(id));
            return;
        }

        // Residente: solo recibe sus propios chats (grupo por residente), no los de todo el edificio.
        if (user is not null && user.Role == "resident"
            && string.Equals(user.OwnerAdmin?.ExternalId, id, StringComparison.Ordinal))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, ResidentGroup(id, user.ExternalId));
            return;
        }

        logger.LogWarning("SupportHub: rejected JoinOwner({Owner}) for {User}", id, user?.ExternalId ?? "unknown");
    }

    public async Task JoinChat(string chatId)
    {
        var id = (chatId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id)) return;

        var user = await ResolveUserAsync();
        if (user is null) return;

        var chat = await db.Set<SupportChat>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ExternalId == id, Context.ConnectionAborted);
        if (chat is null) return;

        var allowed = user.Role == "admin"
            ? await ManagesTenantAsync(user, chat.OwnerAdminId)
            : string.Equals(chat.OwnerAdminId, user.OwnerAdmin?.ExternalId, StringComparison.Ordinal)
              && string.Equals(chat.ResidentId, user.ExternalId, StringComparison.Ordinal);

        if (!allowed)
        {
            logger.LogWarning("SupportHub: rejected JoinChat({Chat}) for {User}", id, user.ExternalId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(id));
    }

    public Task LeaveChat(string chatId)
    {
        var id = (chatId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id)) return Task.CompletedTask;
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroup(id));
    }

    /// <summary>
    /// El administrador gestiona ese condominio: es el suyo, un edificio de su Portafolio o uno donde lo
    /// asignaron como administrador. La junta (solo lectura) no recibe los avisos de gestión.
    /// </summary>
    private async Task<bool> ManagesTenantAsync(BuildingFex.Api.Iam.Domain.Model.Aggregates.User user, string? tenantExternalId)
    {
        if (user.Role != "admin" || string.IsNullOrEmpty(tenantExternalId)) return false;
        if (string.Equals(user.ExternalId, tenantExternalId, StringComparison.Ordinal)) return true;
        var ct = Context.ConnectionAborted;
        if (await db.Set<BuildingFex.Api.Iam.Domain.Model.Aggregates.User>()
                .AnyAsync(u => u.ExternalId == tenantExternalId && u.ParentAdminId == user.Id, ct))
            return true;
        return await db.Set<BuildingMember>()
            .AnyAsync(m => m.UserId == user.Id && m.Role == MemberRoles.Admin && m.OwnerAdmin!.ExternalId == tenantExternalId, ct);
    }

    private async Task<BuildingFex.Api.Iam.Domain.Model.Aggregates.User?> ResolveUserAsync()
    {
        var uid = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? Context.User?.FindFirstValue("user_id")
                  ?? Context.User?.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(uid)) return null;
        return await CallerContextAccessor.FindUserByFirebaseUidAsync(userRepository, uid, Context.ConnectionAborted);
    }

    public static string OwnerGroup(string ownerAdminId) => $"support-owner:{ownerAdminId}";
    public static string ChatGroup(string chatId) => $"support-chat:{chatId}";
    public static string ResidentGroup(string ownerAdminId, string residentId) =>
        $"support-resident:{ownerAdminId}:{residentId}";
}
