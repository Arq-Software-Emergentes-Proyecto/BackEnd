namespace BuildingFex.Api.Iam.Interfaces.Rest.Resources;

public record UserResource(
    string Id,
    string Name,
    string Email,
    string Role,
    string? Floor,
    string? Code,
    string? OwnerAdminId,
    string? Dni,
    string? Address,
    string? Company,
    string? Ruc,
    string? AdmissionDate,
    bool? HasCredentials = null,
    string? SubscriptionPlanId = null,
    string? SubscriptionPaidUntil = null,
    int? ResidentLimit = null,
    bool? BillFromNextMonth = null,
    string? AuthProvider = null,
    string? GoogleSub = null,
    string? InviteToken = null,
    string? InviteExpiresAt = null,
    /// <summary>ACTIVE | DEACTIVATED</summary>
    string? AccountStatus = null,
    string? DeactivatedAt = null,
    /// <summary>EMAIL_PASSWORD | GOOGLE</summary>
    string? IdentityProvider = null,
    /// <summary>administrator | board: quién registró la cuenta del condominio.</summary>
    string? AccountKind = null);

/// <summary>Tras Google, el cliente usa el idToken de Firebase (custom claims). Token App JWT ya no se emite.</summary>
public record AuthenticatedUserResource(
    UserResource User,
    string? Token = null,
    bool UseFirebaseIdToken = true);

public record SignInResource(string Email, string Password);

public record SignInGoogleResource(
    string Email,
    string? GoogleSub = null,
    string? IdToken = null,
    string? Provider = null);

public record CompleteResidentGoogleResource(
    string? Code = null,
    string? Email = null,
    string? Name = null,
    string? GoogleSub = null,
    string? IdToken = null,
    string? Provider = null,
    /// <summary>Invite token 128-bit (preferido). `Code` queda como alias legacy.</summary>
    string? Token = null);

public record RegisterAdminResource(
    string Name,
    string Email,
    string Password,
    string? Dni,
    string? Address,
    string? Company,
    string? Ruc);

public record CreateUserCompatResource(
    string? Id,
    string? Name,
    string? Floor,
    string? Code,
    string? Email,
    string? Password,
    string? Role,
    string? AdmissionDate,
    string? OwnerAdminId,
    string? Dni = null,
    string? Address = null,
    string? Company = null,
    string? Ruc = null,
    bool? BillFromNextMonth = null);

public record UpdateResidentCredentialsResource(
    string? Email = null,
    string? Password = null,
    bool? BillFromNextMonth = null,
    string? Name = null,
    string? Floor = null,
    string? Code = null);

public record SetResidentCredentialsByCodeResource(string Code, string Email, string Password);

public record SubscriptionResponseResource(
    string PlanId,
    int ResidentLimit,
    int ResidentsCount,
    decimal MonthlyPricePen,
    string? PaidUntil,
    bool IsPaid);

public record ChangeSubscriptionPlanResource(string PlanId);

public record SubscriptionCheckoutResource(string PlanId, string? FrontendBaseUrl = null);

public record ConfirmSubscriptionResource(string PlanId, long? PaymentId, bool Demo = false);
