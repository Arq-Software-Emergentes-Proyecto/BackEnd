namespace BuildingFex.Api.Iam.Domain.Model;

public enum IamError
{
    InvalidCredentials,
    EmailNotFound,
    InvalidPassword,
    EmailAlreadyExists,
    UserNotFound,
    ResidentFieldsRequired,
    ResidentCodeAlreadyExists,
    ResidentOwnerRequired,
    ResidentNotFound,
    ResidentPlanLimitReached,
    ResidentOnly,
    ResidentGoogleRequired,
    ResidentUseGoogle,
    /// <summary>Obsoleto: el auto-registro de admins ya no se cierra (cada admin = condominio independiente, RF015).</summary>
    AdminBootstrapClosed,
    /// <summary>Firebase idToken ausente o inválido.</summary>
    InvalidIdToken,
    /// <summary>El caller no es admin del tenant.</summary>
    AdminRequired,
    /// <summary>Recurso fuera del condominio del caller.</summary>
    ForbiddenTenant,
    InviteExpired,
    InviteAlreadyUsed,
    InviteEmailMismatch,
    /// <summary>La invitación es para un correo concreto y la cuenta de Firebase aún no lo verificó.</summary>
    InviteEmailUnverified,
    UseFirebaseAuth,
    /// <summary>La prueba Free de 30 días venció: hay que elegir un plan de pago para ampliar el padrón.</summary>
    SubscriptionTrialExpired,
    /// <summary>Suscripción suspendida o cancelada.</summary>
    SubscriptionInactive,
    /// <summary>Cuenta desactivada por el administrador (403 ACCOUNT_DEACTIVATED).</summary>
    AccountDeactivated,
    /// <summary>Correo con formato inválido (Email value object).</summary>
    InvalidEmail,
}
