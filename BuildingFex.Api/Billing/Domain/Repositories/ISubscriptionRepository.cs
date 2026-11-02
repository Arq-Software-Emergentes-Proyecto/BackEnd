using BuildingFex.Api.Billing.Domain.Model.Aggregates;
using BuildingFex.Api.Shared.Domain.Repositories;

namespace BuildingFex.Api.Billing.Domain.Repositories;

public interface ISubscriptionRepository : IBaseRepository<Subscription>
{
    /// <summary>Suscripción del condominio (tenant = users.id del admin).</summary>
    Task<Subscription?> FindByOwnerAdminIdAsync(int ownerAdminId, CancellationToken cancellationToken = default);

    /// <summary>Cobro ya registrado para ese id de pago de Mercado Pago (anti-replay).</summary>
    Task<SubscriptionPayment?> FindPaymentByMpIdAsync(string mpPaymentId, CancellationToken cancellationToken = default);

    Task AddPaymentAsync(SubscriptionPayment payment, CancellationToken cancellationToken = default);

    Task<SubscriptionCheckout?> FindCheckoutAsync(string externalId, CancellationToken cancellationToken = default);

    Task AddCheckoutAsync(SubscriptionCheckout checkout, CancellationToken cancellationToken = default);

    /// <summary>Descarta cambios en memoria de una entidad rastreada (tras un SaveChanges fallido).</summary>
    Task ReloadAsync(object entity, CancellationToken cancellationToken = default);

    /// <summary>Deja de rastrear una entidad agregada que no llegó a guardarse.</summary>
    void Detach(object entity);
}
