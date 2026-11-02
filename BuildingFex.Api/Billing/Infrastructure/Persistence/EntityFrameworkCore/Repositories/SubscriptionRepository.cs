using BuildingFex.Api.Billing.Domain.Model.Aggregates;
using BuildingFex.Api.Billing.Domain.Repositories;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Billing.Infrastructure.Persistence.EntityFrameworkCore.Repositories;

public class SubscriptionRepository(AppDbContext context)
    : BaseRepository<Subscription>(context), ISubscriptionRepository
{
    public async Task<Subscription?> FindByOwnerAdminIdAsync(int ownerAdminId, CancellationToken cancellationToken = default)
        => await Context.Set<Subscription>()
            .FirstOrDefaultAsync(s => s.OwnerAdminId == ownerAdminId, cancellationToken);

    public async Task<SubscriptionPayment?> FindPaymentByMpIdAsync(string mpPaymentId, CancellationToken cancellationToken = default)
    {
        var id = (mpPaymentId ?? string.Empty).Trim();
        return await Context.Set<SubscriptionPayment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.MpPaymentId == id, cancellationToken);
    }

    public async Task AddPaymentAsync(SubscriptionPayment payment, CancellationToken cancellationToken = default)
        => await Context.Set<SubscriptionPayment>().AddAsync(payment, cancellationToken);

    public async Task<SubscriptionCheckout?> FindCheckoutAsync(string externalId, CancellationToken cancellationToken = default)
    {
        var id = (externalId ?? string.Empty).Trim();
        if (id.Length == 0) return null;
        return await Context.Set<SubscriptionCheckout>()
            .FirstOrDefaultAsync(c => c.ExternalId == id, cancellationToken);
    }

    public async Task AddCheckoutAsync(SubscriptionCheckout checkout, CancellationToken cancellationToken = default)
        => await Context.Set<SubscriptionCheckout>().AddAsync(checkout, cancellationToken);

    public async Task ReloadAsync(object entity, CancellationToken cancellationToken = default)
    {
        var entry = Context.Entry(entity);
        if (entry.State is EntityState.Added or EntityState.Detached)
        {
            entry.State = EntityState.Detached;
            return;
        }

        await entry.ReloadAsync(cancellationToken);
    }

    public void Detach(object entity) => Context.Entry(entity).State = EntityState.Detached;
}
