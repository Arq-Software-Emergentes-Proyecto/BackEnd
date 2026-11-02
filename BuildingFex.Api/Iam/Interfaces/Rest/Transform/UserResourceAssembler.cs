using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Interfaces.Rest.Resources;

namespace BuildingFex.Api.Iam.Interfaces.Rest.Transform;

public static class UserResourceAssembler
{
    public static UserResource ToResource(User user) =>
        new(
            user.ExternalId,
            user.Name,
            user.Email,
            user.Role,
            user.Floor,
            user.Code,
            user.OwnerAdmin?.ExternalId,
            user.Dni,
            user.Address,
            user.Company,
            user.Ruc,
            user.AdmissionDate?.ToString("yyyy-MM-dd"),
            user.Role == "resident" ? user.HasCredentials : null,
            user.Role == "admin" ? SubscriptionPlans.Normalize(user.SubscriptionPlanId) : null,
            user.Role == "admin" && user.SubscriptionPaidUntil.HasValue
                ? user.SubscriptionPaidUntil.Value.ToString("o")
                : null,
            user.Role == "admin" ? SubscriptionPlans.MaxResidents(SubscriptionPlans.EffectivePlanId(user.SubscriptionPlanId, user.SubscriptionPaidUntil, DateTimeOffset.UtcNow)) : null,
            user.Role == "resident" ? user.BillFromNextMonth : null,
            string.IsNullOrWhiteSpace(user.AuthProvider) ? null : user.AuthProvider,
            user.GoogleSub,
            // Solo mientras la invitación esté viva (admin copia el link).
            user.Role == "resident" && !user.InviteUsedAt.HasValue ? user.InviteToken : null,
            user.Role == "resident" && !user.InviteUsedAt.HasValue && user.InviteExpiresAt.HasValue
                ? user.InviteExpiresAt.Value.ToString("o")
                : null,
            user.IsActive ? AccountStatus.Active : AccountStatus.Deactivated,
            user.DeactivatedAt?.ToString("o"),
            user.IdentityProvider,
            user.Role == "admin" ? user.AccountKind ?? User.AccountKindAdministrator : null);
}
