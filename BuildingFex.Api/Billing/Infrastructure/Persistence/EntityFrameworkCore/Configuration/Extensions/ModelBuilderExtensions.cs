using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Billing.Domain.Model.Aggregates;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Billing.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;

public static class ModelBuilderExtensions
{
    /// <summary>Tabla subscriptions (también creada/actualizada en SchemaUpgrade para DBs existentes).</summary>
    public static void ApplyBillingConfiguration(this ModelBuilder builder)
    {
        builder.Entity<Subscription>(entity =>
        {
            entity.ToTable("subscriptions");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedOnAdd();
            entity.Property(s => s.OwnerAdminId).IsRequired();
            entity.HasIndex(s => s.OwnerAdminId).IsUnique();
            entity.Property(s => s.BuildingId);
            entity.Property(s => s.PlanId).HasMaxLength(20).IsRequired().HasDefaultValue(SubscriptionPlanIds.Free);
            entity.Property(s => s.Status).HasMaxLength(20).IsRequired().HasDefaultValue(SubscriptionStatus.Trial);
            entity.Property(s => s.TrialEndsAt);
            entity.Property(s => s.PaidUntil);
            entity.Property(s => s.FailedCharges).HasDefaultValue(0);
            entity.Property(s => s.BilledDepartments);
            entity.HasIndex(s => new { s.Status, s.TrialEndsAt });

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(s => s.OwnerAdminId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Cobros MP de suscripción: mp_payment_id único → un pago se aplica una sola vez (anti-replay).
        builder.Entity<SubscriptionPayment>(entity =>
        {
            entity.ToTable("subscription_payments");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedOnAdd();
            entity.Property(p => p.MpPaymentId).HasMaxLength(64).IsRequired();
            entity.HasIndex(p => p.MpPaymentId).IsUnique().HasDatabaseName(BillingIndexes.SubscriptionPaymentMpId);
            entity.Property(p => p.OwnerAdminId).IsRequired();
            entity.HasIndex(p => p.OwnerAdminId).HasDatabaseName("ix_subscription_payments_owner_admin_id");
            entity.Property(p => p.PlanId).HasMaxLength(20).IsRequired();
            entity.Property(p => p.Amount).HasPrecision(18, 2);
            entity.Property(p => p.ExpectedAmount).HasPrecision(18, 2);
            entity.Property(p => p.Currency).HasMaxLength(8);
            entity.Property(p => p.CheckoutExternalId).HasMaxLength(64);
            entity.Property(p => p.Status).HasMaxLength(32).IsRequired();
            entity.Property(p => p.Source).HasMaxLength(16).IsRequired();
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(p => p.OwnerAdminId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Snapshot del checkout de suscripción (monto fijado al crear la preferencia MP).
        builder.Entity<SubscriptionCheckout>(entity =>
        {
            entity.ToTable("subscription_checkouts");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();
            entity.Property(c => c.ExternalId).HasMaxLength(64).IsRequired();
            entity.HasIndex(c => c.ExternalId).IsUnique().HasDatabaseName("ux_subscription_checkouts_external_id");
            entity.Property(c => c.OwnerAdminId).IsRequired();
            entity.HasIndex(c => c.OwnerAdminId).HasDatabaseName("ix_subscription_checkouts_owner_admin_id");
            entity.Property(c => c.PlanId).HasMaxLength(20).IsRequired();
            entity.Property(c => c.Amount).HasPrecision(18, 2);
            entity.Property(c => c.Status).HasMaxLength(20).IsRequired();
            entity.Property(c => c.MpPaymentId).HasMaxLength(64);
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(c => c.OwnerAdminId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
