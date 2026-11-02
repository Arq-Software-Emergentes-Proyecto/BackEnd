using BuildingFex.Api.Billing.Application.Internal;
using BuildingFex.Api.Iam.Application.CommandServices;
using BuildingFex.Api.Iam.Application.Internal.OutboundServices;
using BuildingFex.Api.Iam.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.Commands;
using BuildingFex.Api.Iam.Domain.Model.ValueObjects;
using BuildingFex.Api.Iam.Domain.Repositories;
using BuildingFex.Api.Iam.Infrastructure.Firebase;
using BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt;
using BuildingFex.Api.Shared.Application.Model;
using BuildingFex.Api.Shared.Domain.Repositories;

namespace BuildingFex.Api.Iam.Application.Internal.CommandServices;

public class UserCommandService(
    IUserRepository userRepository,
    ITokenService tokenService,
    IHashingService hashingService,
    IFirebaseIdTokenVerifier idTokenVerifier,
    IFirebaseAuthAdminService firebaseAuthAdmin,
    IUnitOfWork unitOfWork,
    ISubscriptionService subscriptionService) : IUserCommandService
{
    // tokenService retained for DI compatibility; App JWT emission removed.
    private readonly ITokenService _ = tokenService;
    public async Task<Result<(User user, string token)>> Handle(
        SignInCommand command,
        CancellationToken cancellationToken = default)
    {
        return Result<(User user, string token)>.Failure(
            IamError.UseFirebaseAuth,
            "Usa Firebase Auth (email/password o Google). El App JWT ya no se emite.");
    }

    public async Task<Result<(User user, string token)>> Handle(
        RegisterAdminCommand command,
        CancellationToken cancellationToken = default)
    {
        return Result<(User user, string token)>.Failure(
            IamError.UseFirebaseAuth,
            "Registro admin vía Firebase + POST /api/v1/profiles.");
    }

    public async Task<Result<(User user, string token)>> Handle(
        SignInGoogleCommand command,
        CancellationToken cancellationToken = default)
    {
        var identity = await idTokenVerifier.VerifyAsync(command.IdToken, cancellationToken);
        if (identity is null)
            return Result<(User user, string token)>.Failure(
                IamError.InvalidIdToken,
                "idToken de Google/Firebase inválido o ausente.");

        var googleSub = identity.Uid;
        var email = string.IsNullOrWhiteSpace(identity.Email)
            ? (command.Email ?? string.Empty).Trim().ToLowerInvariant()
            : identity.Email;

        User? user = await userRepository.FindByGoogleSubAsync(googleSub, cancellationToken);

        // Fallback por email solo si Firebase lo verificó (Google). Una cuenta email/contraseña
        // con email no verificado no puede apropiarse de un residente por coincidencia de email.
        if (user is null && identity.EmailVerified && !string.IsNullOrWhiteSpace(email))
            user = await userRepository.FindByEmailAsync(email, cancellationToken);

        if (user is null)
            return Result<(User user, string token)>.Failure(
                IamError.EmailNotFound,
                "No hay un residente invitado con ese Google/email.");

        if (user.Role != "resident")
            return Result<(User user, string token)>.Failure(
                IamError.ResidentOnly,
                "Solo residentes invitados pueden usar este login.");

        if (!user.IsActive)
            return Result<(User user, string token)>.Failure(
                IamError.AccountDeactivated,
                "Tu cuenta fue desactivada por la administración del condominio.");

        if (!user.HasCredentials
            && !string.Equals(user.AuthProvider, User.AuthProviderGoogle, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(user.GoogleSub))
        {
            return Result<(User user, string token)>.Failure(
                IamError.ResidentGoogleRequired,
                "El residente debe completar la invitación con Google primero.");
        }

        if ((string.IsNullOrWhiteSpace(user.GoogleSub)
             || !string.Equals(user.GoogleSub, googleSub, StringComparison.Ordinal))
            && identity.EmailVerified)
        {
            user.ActivateGoogle(
                string.IsNullOrWhiteSpace(email) ? user.Email : email,
                googleSub);
            userRepository.Update(user);
            await unitOfWork.CompleteAsync(cancellationToken);
            user = await userRepository.FindByExternalIdAsync(user.ExternalId, cancellationToken) ?? user;
        }

        await SyncResidentClaimsAsync(user, googleSub, cancellationToken);

        // Token vacío: el cliente usa el idToken de Firebase (custom claims).
        return Result<(User user, string token)>.Success((user, string.Empty));
    }

    public async Task<Result<(User user, string token)>> Handle(
        CompleteResidentGoogleCommand command,
        CancellationToken cancellationToken = default)
    {
        var identity = await idTokenVerifier.VerifyAsync(command.IdToken, cancellationToken);
        if (identity is null)
            return Result<(User user, string token)>.Failure(
                IamError.InvalidIdToken,
                "idToken de Google/Firebase inválido o ausente.");

        var inviteToken = (command.Code ?? string.Empty).Trim();
        var googleSub = identity.Uid;
        // El email sale siempre del token verificado por Firebase; nunca del body.
        var email = (identity.Email ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(inviteToken) || string.IsNullOrWhiteSpace(email))
            return Result<(User user, string token)>.Failure(
                IamError.ResidentFieldsRequired,
                "Token de invitación e email de Google son obligatorios.");

        var user = await userRepository.FindByInviteTokenAsync(inviteToken, cancellationToken);
        if (user is null || user.Role != "resident")
            return Result<(User user, string token)>.Failure(
                IamError.ResidentNotFound,
                "Invitación no encontrada.");

        if (!user.IsActive)
            return Result<(User user, string token)>.Failure(
                IamError.AccountDeactivated,
                "Tu cuenta fue desactivada por la administración del condominio.");

        if (user.InviteUsedAt.HasValue)
            return Result<(User user, string token)>.Failure(
                IamError.InviteAlreadyUsed,
                "Esta invitación ya fue usada.");

        if (user.InviteExpiresAt.HasValue && user.InviteExpiresAt.Value < DateTimeOffset.UtcNow)
            return Result<(User user, string token)>.Failure(
                IamError.InviteExpired,
                "Esta invitación expiró.");

        if (!user.IsInviteValid(inviteToken))
            return Result<(User user, string token)>.Failure(
                IamError.ResidentNotFound,
                "Invitación no válida.");

        if (!string.IsNullOrWhiteSpace(user.InviteEmail))
        {
            if (!string.Equals(user.InviteEmail, email, StringComparison.OrdinalIgnoreCase))
                return Result<(User user, string token)>.Failure(
                    IamError.InviteEmailMismatch,
                    "El email de Google no coincide con el de la invitación.");

            // Invitación dirigida a un correo concreto: hay que demostrar que es suyo
            // (Google siempre lo verifica; una cuenta con contraseña aún no verificada, no).
            if (!identity.EmailVerified)
                return Result<(User user, string token)>.Failure(
                    IamError.InviteEmailUnverified,
                    "Verifica tu correo o entra con Google para usar esta invitación.");
        }

        var emailOwner = await userRepository.FindByEmailAsync(email, cancellationToken);
        if (emailOwner is not null && emailOwner.ExternalId != user.ExternalId)
            return Result<(User user, string token)>.Failure(
                IamError.EmailAlreadyExists,
                "El email ya está registrado.");

        var googleOwner = await userRepository.FindByGoogleSubAsync(googleSub, cancellationToken);
        if (googleOwner is not null && googleOwner.ExternalId != user.ExternalId)
            return Result<(User user, string token)>.Failure(
                IamError.EmailAlreadyExists,
                "Esta cuenta de Google ya está vinculada a otro residente.");

        // Google → ActivateGoogle; email/contraseña Firebase → ActivateFirebasePassword (mismo uid en google_sub).
        if (identity.IsPasswordProvider)
            user.ActivateFirebasePassword(email, googleSub, command.Name);
        else
            user.ActivateGoogle(email, googleSub, command.Name);
        userRepository.Update(user);
        await unitOfWork.CompleteAsync(cancellationToken);

        var updated = await userRepository.FindByExternalIdAsync(user.ExternalId, cancellationToken) ?? user;
        await SyncResidentClaimsAsync(updated, googleSub, cancellationToken);

        return Result<(User user, string token)>.Success((updated, string.Empty));
    }

    private async Task SyncResidentClaimsAsync(User user, string firebaseUid, CancellationToken ct)
    {
        if (!firebaseAuthAdmin.IsConfigured) return;

        var reloaded = await userRepository.FindByExternalIdAsync(user.ExternalId, ct) ?? user;
        var ownerExternalId = reloaded.OwnerAdmin?.ExternalId ?? string.Empty;
        await firebaseAuthAdmin.SetClaimsAsync(firebaseUid, "resident", ownerExternalId, ct);
    }

    public async Task<Result<User>> Handle(
        UpsertFirebaseAdminCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.ExternalId) || string.IsNullOrWhiteSpace(command.Email))
            return Result<User>.Failure(
                IamError.ResidentFieldsRequired,
                "id (Firebase uid) y email son obligatorios.");

        if (!Email.IsValid(command.Email))
            return Result<User>.Failure(IamError.InvalidEmail, "El correo electrónico no es válido.");

        var existingById = await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (existingById is not null)
        {
            if (existingById.Role != "admin")
                return Result<User>.Failure(IamError.EmailAlreadyExists, "El id ya pertenece a otro usuario.");

            existingById.UpdateAdminProfile(
                command.Name,
                command.Dni,
                command.Address,
                command.Company,
                command.Ruc);
            userRepository.Update(existingById);
            await unitOfWork.CompleteAsync(cancellationToken);
            await firebaseAuthAdmin.SetClaimsAsync(command.ExternalId.Trim(), "admin", command.ExternalId.Trim(), cancellationToken);
            return Result<User>.Success(
                await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken) ?? existingById);
        }

        // Un uid que ya es residente (vinculado por google_sub) no puede abrir un condominio propio.
        var residentByUid = await userRepository.FindByGoogleSubAsync(command.ExternalId.Trim(), cancellationToken);
        if (residentByUid is not null)
            return Result<User>.Failure(IamError.EmailAlreadyExists, "Esta cuenta ya pertenece a un residente.");

        // Nunca se adopta la fila de otra cuenta por coincidencia de email: devolvería y sobrescribiría
        // los datos de ese usuario. El dueño real entra con su propia cuenta de Firebase.
        if (await userRepository.FindByEmailAsync(command.Email, cancellationToken) is not null)
            return Result<User>.Failure(IamError.EmailAlreadyExists, "El email ya está registrado.");

        // RF015 / TS001: SaaS multi-tenant — cada admin nuevo obtiene su propio espacio de gestión
        // (tenant = su users.id). Antes solo el primer admin del sistema podía registrarse (ADMIN_BOOTSTRAP_CLOSED).
        var user = User.CreateAdmin(
            command.ExternalId.Trim(),
            command.Name,
            command.Email,
            User.FirebasePasswordPlaceholder,
            command.Dni,
            command.Address,
            command.Company,
            command.Ruc,
            User.AuthProviderFirebase);

        await userRepository.AddAsync(user, cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);

        // Billing: prueba Free de 30 días con funciones de Essential desde el alta (informe 1.2).
        await subscriptionService.StartTrialAsync(user, cancellationToken);

        await firebaseAuthAdmin.SetClaimsAsync(user.ExternalId, "admin", user.ExternalId, cancellationToken);

        return Result<User>.Success(
            await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken) ?? user);
    }

    public async Task<Result<User>> Handle(
        CreateResidentCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name) ||
            string.IsNullOrWhiteSpace(command.Floor) ||
            string.IsNullOrWhiteSpace(command.Code))
            return Result<User>.Failure(
                IamError.ResidentFieldsRequired,
                "Nombre, piso y código son obligatorios.");

        if (string.IsNullOrWhiteSpace(command.OwnerAdminExternalId))
            return Result<User>.Failure(
                IamError.ResidentOwnerRequired,
                "ownerAdminId es obligatorio.");

        if (!string.IsNullOrWhiteSpace(command.InviteEmail) && !Email.IsValid(command.InviteEmail))
            return Result<User>.Failure(IamError.InvalidEmail, "El correo de la invitación no es válido.");

        var owner = await userRepository.FindByExternalIdAsync(command.OwnerAdminExternalId, cancellationToken);
        if (owner is null || owner.Role != "admin")
            return Result<User>.Failure(
                IamError.ResidentOwnerRequired,
                "Administrador no encontrado.");

        if (await userRepository.ExistsResidentByCodeAsync(command.Code, owner.Id, cancellationToken))
            return Result<User>.Failure(
                IamError.ResidentCodeAlreadyExists,
                "Ya existe un residente con ese código.");

        // RF028: límite de departamentos del plan + prueba Free vigente (Billing).
        var gate = await subscriptionService.CheckRosterGrowthAsync(owner, 1, cancellationToken);
        if (!gate.Allowed)
            return Result<User>.Failure(
                gate.ErrorCode switch
                {
                    SubscriptionService.ErrorTrialExpired => IamError.SubscriptionTrialExpired,
                    SubscriptionService.ErrorSubscriptionInactive => IamError.SubscriptionInactive,
                    _ => IamError.ResidentPlanLimitReached,
                },
                gate.Message ?? $"Límite del plan alcanzado (máximo {gate.Limit} residentes). Cambia tu plan en Ajustes.");

        DateOnly? admissionDate = null;
        if (!string.IsNullOrWhiteSpace(command.AdmissionDate) &&
            DateOnly.TryParse(command.AdmissionDate, out var parsed))
            admissionDate = parsed;

        var externalId = string.IsNullOrWhiteSpace(command.ExternalId)
            ? $"resident-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : command.ExternalId.Trim();

        var user = User.CreateResident(
            externalId,
            command.Name,
            email: null,
            passwordHash: string.Empty,
            command.Floor,
            command.Code,
            owner.Id,
            admissionDate,
            command.BillFromNextMonth,
            inviteEmail: command.InviteEmail);

        await userRepository.AddAsync(user, cancellationToken);
        await unitOfWork.CompleteAsync(cancellationToken);

        var created = await userRepository.FindByExternalIdAsync(externalId, cancellationToken);
        return Result<User>.Success(created!);
    }

    public async Task<Result<User>> Handle(
        UpdateResidentCredentialsCommand command,
        CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (user is null || user.Role != "resident")
            return Result<User>.Failure(
                IamError.ResidentNotFound,
                "Residente no encontrado.");

        if (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.Password))
            return Result<User>.Failure(
                IamError.ResidentFieldsRequired,
                "Email y contraseña son obligatorios.");

        if (command.Password.StartsWith("google:", StringComparison.OrdinalIgnoreCase))
            return Result<User>.Failure(
                IamError.InvalidIdToken,
                "Usa complete-google con idToken verificado; el atajo google:{sub} ya no está permitido.");

        var emailOwner = await userRepository.FindByEmailAsync(command.Email, cancellationToken);
        if (emailOwner is not null && emailOwner.ExternalId != user.ExternalId)
            return Result<User>.Failure(
                IamError.EmailAlreadyExists,
                "El email ya está registrado.");

        user.UpdateCredentials(command.Email, hashingService.HashPassword(command.Password));

        userRepository.Update(user);
        await unitOfWork.CompleteAsync(cancellationToken);

        var updated = await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        return Result<User>.Success(updated!);
    }

    public async Task<Result<User>> Handle(
        PatchResidentCommand command,
        CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (user is null || user.Role != "resident")
            return Result<User>.Failure(
                IamError.ResidentNotFound,
                "Residente no encontrado.");

        if (command.BillFromNextMonth.HasValue)
            user.SetBillFromNextMonth(command.BillFromNextMonth.Value);

        var newCode = command.Code?.Trim();
        if (!string.IsNullOrEmpty(newCode)
            && !string.Equals(newCode, user.Code, StringComparison.OrdinalIgnoreCase)
            && user.OwnerAdminId is int ownerId
            && await userRepository.ExistsResidentByCodeAsync(newCode, ownerId, cancellationToken))
            return Result<User>.Failure(
                IamError.ResidentCodeAlreadyExists,
                "Ya existe un residente con ese código.");

        if (command.Name is not null || command.Floor is not null || command.Code is not null)
            user.UpdateResidentProfile(command.Name, command.Floor, command.Code);

        if (!string.IsNullOrWhiteSpace(command.Email) && !string.IsNullOrWhiteSpace(command.Password))
        {
            if (command.Password.StartsWith("google:", StringComparison.OrdinalIgnoreCase))
                return Result<User>.Failure(
                    IamError.InvalidIdToken,
                    "Usa complete-google con idToken verificado; el atajo google:{sub} ya no está permitido.");

            var emailOwner = await userRepository.FindByEmailAsync(command.Email, cancellationToken);
            if (emailOwner is not null && emailOwner.ExternalId != user.ExternalId)
                return Result<User>.Failure(
                    IamError.EmailAlreadyExists,
                    "El email ya está registrado.");

            user.UpdateCredentials(command.Email, hashingService.HashPassword(command.Password));
        }

        userRepository.Update(user);
        await unitOfWork.CompleteAsync(cancellationToken);

        var updated = await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        return Result<User>.Success(updated!);
    }

    public async Task<Result<User>> Handle(
        DeleteResidentCommand command,
        CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (user is null || user.Role != "resident")
            return Result<User>.Failure(
                IamError.ResidentNotFound,
                "Residente no encontrado.");

        if (!string.IsNullOrWhiteSpace(user.GoogleSub) && firebaseAuthAdmin.IsConfigured)
            await firebaseAuthAdmin.ClearClaimsAndRevokeAsync(user.GoogleSub, cancellationToken);

        userRepository.Remove(user);
        await unitOfWork.CompleteAsync(cancellationToken);

        return Result<User>.Success(user);
    }

    /// <summary>
    /// DeactivateAccount (Tabla 54): marca la cuenta DEACTIVATED y revoca sus sesiones Firebase
    /// (refresh tokens + custom claims). Los datos del residente se conservan (a diferencia de borrar).
    /// </summary>
    public async Task<Result<User>> Handle(
        DeactivateAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (user is null || user.Role != UserRoles.Resident)
            return Result<User>.Failure(IamError.ResidentNotFound, "Residente no encontrado.");

        user.Deactivate();
        userRepository.Update(user);
        await unitOfWork.CompleteAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(user.GoogleSub) && firebaseAuthAdmin.IsConfigured)
        {
            try { await firebaseAuthAdmin.ClearClaimsAndRevokeAsync(user.GoogleSub, cancellationToken); }
            catch { /* best-effort: el bloqueo real lo aplica la API (403 ACCOUNT_DEACTIVATED) */ }
        }

        return Result<User>.Success(user);
    }

    public async Task<Result<User>> Handle(
        ReactivateAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        var user = await userRepository.FindByExternalIdAsync(command.ExternalId, cancellationToken);
        if (user is null || user.Role != UserRoles.Resident)
            return Result<User>.Failure(IamError.ResidentNotFound, "Residente no encontrado.");

        user.Reactivate();
        userRepository.Update(user);
        await unitOfWork.CompleteAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(user.GoogleSub))
        {
            try { await SyncResidentClaimsAsync(user, user.GoogleSub, cancellationToken); }
            catch { /* best-effort */ }
        }

        return Result<User>.Success(user);
    }
}
