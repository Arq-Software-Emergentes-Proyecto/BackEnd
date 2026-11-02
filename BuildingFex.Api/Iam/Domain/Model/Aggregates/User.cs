using System.Text.Json.Serialization;
using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.ValueObjects;
using BuildingFex.Api.Shared.Domain.Model.Entities;

namespace BuildingFex.Api.Iam.Domain.Model.Aggregates;

/// <summary>
/// Aggregate root de IAM. Unifica admin y residente (compatible con el frontend Vue).
/// </summary>
public class User : IAuditableEntity
{
    public const string AuthProviderPassword = "password";
    public const string AuthProviderGoogle = "google";
    public const string AuthProviderFirebase = "firebase";

    /// <summary>Cuenta técnica de un edificio adicional del plan Portafolio: no inicia sesión.</summary>
    public const string AuthProviderManaged = "managed";

    /// <summary>Quién creó la cuenta del condominio: la administración o la junta directiva.</summary>
    public const string AccountKindAdministrator = "administrator";
    public const string AccountKindBoard = "board";
    public const string FirebasePasswordPlaceholder = "!firebase-auth!";

    public int Id { get; private set; }

    /// <summary>ID string compatible con json-server / Firebase uid (admins).</summary>
    public string ExternalId { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;

    [JsonIgnore]
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>admin | resident</summary>
    public string Role { get; private set; } = UserRoles.Resident;

    /// <summary>password | google | firebase</summary>
    public string AuthProvider { get; private set; } = AuthProviderPassword;

    /// <summary>Firebase uid / Google subject for residents (and optionally admins).</summary>
    public string? GoogleSub { get; private set; }

    // Admin
    public string? Dni { get; private set; }
    public string? Address { get; private set; }
    public string? Company { get; private set; }
    public string? Ruc { get; private set; }

    // Resident
    public string? Floor { get; private set; }
    /// <summary>Código de unidad (piso/depto) visible. No es secreto de invitación.</summary>
    public string? Code { get; private set; }
    /// <summary>Token de invitación (≥128 bits). Va en el link; un solo uso.</summary>
    public string? InviteToken { get; private set; }
    public DateTimeOffset? InviteExpiresAt { get; private set; }
    public DateTimeOffset? InviteUsedAt { get; private set; }
    /// <summary>Si se fijó, el email de Google debe coincidir al completar.</summary>
    public string? InviteEmail { get; private set; }
    public DateOnly? AdmissionDate { get; private set; }
    public bool BillFromNextMonth { get; private set; }

    /// <summary>Multi-tenancy: residente pertenece a este admin (FK interna).</summary>
    public int? OwnerAdminId { get; private set; }

    public User? OwnerAdmin { get; private set; }

    /// <summary>
    /// Portafolio: edificio adicional de una cuenta. La cuenta técnica del edificio (tenant de sus datos)
    /// apunta a la cuenta matriz que paga la suscripción y lo administra.
    /// </summary>
    public int? ParentAdminId { get; private set; }

    public User? ParentAdmin { get; private set; }

    /// <summary>administrator | board: quién registró la cuenta (solo dueños de condominio).</summary>
    public string? AccountKind { get; private set; }

    /// <summary>Cuenta técnica de un edificio de Portafolio (no inicia sesión).</summary>
    public bool IsManagedBuildingAccount => ParentAdminId.HasValue;

    /// <summary>Cuenta que paga la suscripción: la matriz en Portafolio, si no la propia.</summary>
    public User BillingAccount => ParentAdmin ?? this;

    /// <summary>
    /// Plan de suscripción del condominio — solo admins (dueño del edificio).
    /// Los residentes no llevan plan; se lee del OwnerAdmin.
    /// </summary>
    public string SubscriptionPlanId { get; private set; } = SubscriptionPlanIds.Free;

    /// <summary>When the current paid period ends (UTC). Null for free or legacy rows.</summary>
    public DateTimeOffset? SubscriptionPaidUntil { get; private set; }

    /// <summary>ACTIVE | DEACTIVATED (Tabla 52 – AccountStatus).</summary>
    public string AccountStatus { get; private set; } = ValueObjects.AccountStatus.Active;

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>isActive(): una cuenta desactivada no inicia sesión ni usa la API.</summary>
    public bool IsActive =>
        !string.Equals(AccountStatus, ValueObjects.AccountStatus.Deactivated, StringComparison.OrdinalIgnoreCase);

    /// <summary>EMAIL_PASSWORD | GOOGLE, derivado de <see cref="AuthProvider"/>.</summary>
    public string IdentityProvider => ValueObjects.IdentityProvider.FromAuthProvider(AuthProvider);

    public bool HasCredentials =>
        string.Equals(AuthProvider, AuthProviderGoogle, StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(GoogleSub)
        || (!string.IsNullOrWhiteSpace(Email)
            && !string.IsNullOrWhiteSpace(PasswordHash)
            && PasswordHash != FirebasePasswordPlaceholder);

    private User()
    {
    }

    public static User CreateAdmin(
        string externalId,
        string name,
        string email,
        string passwordHash,
        string? dni = null,
        string? address = null,
        string? company = null,
        string? ruc = null,
        string authProvider = AuthProviderPassword)
    {
        return new User
        {
            ExternalId = externalId.Trim(),
            Name = name.Trim(),
            Email = ValueObjects.Email.Create(email).Value,
            PasswordHash = passwordHash,
            Role = UserRoles.Admin,
            AuthProvider = authProvider,
            Dni = dni,
            Address = address,
            Company = company,
            Ruc = ruc,
        };
    }

    /// <summary>Miembro de la junta directiva invitado a ver un condominio (solo lectura).</summary>
    public static User CreateBoardMember(string firebaseUid, string name, string email)
    {
        return new User
        {
            ExternalId = firebaseUid.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? "Junta directiva" : name.Trim(),
            Email = ValueObjects.Email.Create(email).Value,
            PasswordHash = FirebasePasswordPlaceholder,
            Role = UserRoles.BoardMember,
            AuthProvider = AuthProviderFirebase,
        };
    }

    /// <summary>
    /// Portafolio: cuenta técnica de un edificio adicional. Es el tenant de los datos del edificio
    /// (owner_admin_id de sus residentes, cuotas, reservas…); la gestiona la cuenta matriz y comparte su plan.
    /// </summary>
    public static User CreateManagedBuildingAccount(User parent, string buildingName)
    {
        if (parent.Role != UserRoles.Admin)
            throw new InvalidOperationException("Only admin accounts can own buildings.");

        var bytes = new byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return new User
        {
            ExternalId = "bldg-" + Convert.ToHexString(bytes).ToLowerInvariant(),
            Name = buildingName.Trim(),
            Email = string.Empty,
            PasswordHash = string.Empty,
            Role = UserRoles.Admin,
            AuthProvider = AuthProviderManaged,
            ParentAdminId = parent.Id,
            AccountKind = parent.AccountKind,
            SubscriptionPlanId = parent.SubscriptionPlanId,
            SubscriptionPaidUntil = parent.SubscriptionPaidUntil,
        };
    }

    /// <summary>Se fija una sola vez, al registrar la cuenta del condominio.</summary>
    public void SetAccountKind(string? kind)
    {
        if (Role != UserRoles.Admin || !string.IsNullOrWhiteSpace(AccountKind)) return;
        AccountKind = string.Equals(kind?.Trim(), AccountKindBoard, StringComparison.OrdinalIgnoreCase)
            ? AccountKindBoard
            : AccountKindAdministrator;
    }

    public static User CreateResident(
        string externalId,
        string name,
        string? email,
        string? passwordHash,
        string floor,
        string code,
        int ownerAdminId,
        DateOnly? admissionDate = null,
        bool billFromNextMonth = false,
        string? inviteEmail = null,
        TimeSpan? inviteTtl = null)
    {
        var inviteToken = GenerateInviteToken();
        var ttl = inviteTtl ?? TimeSpan.FromDays(7);
        return new User
        {
            ExternalId = externalId.Trim(),
            Name = name.Trim(),
            // El residente puede crearse sin correo (lo fija al aceptar la invitación); si viene, se valida.
            Email = string.IsNullOrWhiteSpace(email) ? string.Empty : ValueObjects.Email.Create(email).Value,
            PasswordHash = passwordHash ?? string.Empty,
            Role = UserRoles.Resident,
            Floor = floor,
            Code = code,
            InviteToken = inviteToken,
            InviteExpiresAt = DateTimeOffset.UtcNow.Add(ttl),
            InviteUsedAt = null,
            InviteEmail = string.IsNullOrWhiteSpace(inviteEmail)
                ? null
                : ValueObjects.Email.Create(inviteEmail).Value,
            OwnerAdminId = ownerAdminId,
            AdmissionDate = admissionDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            BillFromNextMonth = billFromNextMonth,
            AuthProvider = AuthProviderPassword,
            // Plan solo del admin/condominio — residentes no lo usan
            SubscriptionPlanId = SubscriptionPlanIds.Free,
        };
    }

    public static string GenerateInviteToken()
    {
        var bytes = new byte[16]; // 128 bits
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public bool IsInviteValid(string token)
    {
        if (string.IsNullOrWhiteSpace(InviteToken) || string.IsNullOrWhiteSpace(token))
            return false;
        if (InviteUsedAt.HasValue)
            return false;
        if (InviteExpiresAt.HasValue && InviteExpiresAt.Value < DateTimeOffset.UtcNow)
            return false;
        return string.Equals(InviteToken, token.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public void ConsumeInvite()
    {
        InviteUsedAt = DateTimeOffset.UtcNow;
    }

    public void UpdateCredentials(string email, string passwordHash)
    {
        if (Role != UserRoles.Resident)
            throw new InvalidOperationException("Only residents can update credentials.");

        Email = email.Trim().ToLowerInvariant();
        PasswordHash = passwordHash;
        AuthProvider = AuthProviderPassword;
    }

    public void ActivateGoogle(string email, string googleSub, string? name = null)
    {
        if (Role != UserRoles.Resident)
            throw new InvalidOperationException("Only residents can activate Google auth.");

        Email = email.Trim().ToLowerInvariant();
        GoogleSub = googleSub.Trim();
        AuthProvider = AuthProviderGoogle;
        PasswordHash = string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
            Name = name.Trim();
        ConsumeInvite();
    }

    /// <summary>
    /// Residente completa la invitación creando una cuenta Firebase email/contraseña (US014).
    /// GoogleSub guarda el uid Firebase (sea de Google o de email/contraseña).
    /// </summary>
    public void ActivateFirebasePassword(string email, string firebaseUid, string? name = null)
    {
        if (Role != UserRoles.Resident)
            throw new InvalidOperationException("Only residents can activate invite credentials.");

        Email = email.Trim().ToLowerInvariant();
        GoogleSub = firebaseUid.Trim();
        AuthProvider = AuthProviderFirebase;
        PasswordHash = string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
            Name = name.Trim();
        ConsumeInvite();
    }

    public void UpdateAdminProfile(string name, string? dni, string? address, string? company, string? ruc)
    {
        if (Role != UserRoles.Admin)
            throw new InvalidOperationException("Only admins have this profile.");

        if (!string.IsNullOrWhiteSpace(name))
            Name = name.Trim();
        Dni = dni;
        Address = address;
        Company = company;
        Ruc = ruc;
    }

    /// <summary>US028: el administrador corrige el nombre o la unidad (piso y código de departamento).</summary>
    public void UpdateResidentProfile(string? name, string? floor, string? code)
    {
        if (Role != UserRoles.Resident)
            throw new InvalidOperationException("Only residents have a unit.");

        if (!string.IsNullOrWhiteSpace(name)) Name = name.Trim();
        if (floor is not null) Floor = string.IsNullOrWhiteSpace(floor) ? null : floor.Trim();
        if (!string.IsNullOrWhiteSpace(code)) Code = code.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetBillFromNextMonth(bool value)
    {
        if (Role != UserRoles.Resident)
            throw new InvalidOperationException("Only residents have billFromNextMonth.");

        BillFromNextMonth = value;
    }

    /// <summary>
    /// deactivate(): la cuenta deja de poder iniciar sesión y cada request autenticado responde
    /// 403 ACCOUNT_DEACTIVATED. Solo residentes: el admin es el dueño del condominio (tenant).
    /// </summary>
    public void Deactivate()
    {
        if (Role != UserRoles.Resident)
            throw new InvalidOperationException("Only resident accounts can be deactivated.");
        if (!IsActive) return;

        AccountStatus = ValueObjects.AccountStatus.Deactivated;
        DeactivatedAt = DateTimeOffset.UtcNow;
    }

    public void Reactivate()
    {
        AccountStatus = ValueObjects.AccountStatus.Active;
        DeactivatedAt = null;
    }

    public void UpdateSubscription(string planId, DateTimeOffset? paidUntil = null)
    {
        if (Role != UserRoles.Admin)
            throw new InvalidOperationException("Only admin accounts have subscription plans.");

        SubscriptionPlanId = SubscriptionPlans.Normalize(planId);
        SubscriptionPaidUntil = SubscriptionPlans.IsPaid(SubscriptionPlanId) ? paidUntil : null;
    }
}
