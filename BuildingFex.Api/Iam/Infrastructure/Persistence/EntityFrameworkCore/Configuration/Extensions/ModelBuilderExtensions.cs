using BuildingFex.Api.Billing.Domain.Model;
using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BuildingFex.Api.Iam.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;

public static class ModelBuilderExtensions
{
    public static void ApplyIamConfiguration(this ModelBuilder builder)
    {
        var dateOnlyConverter = new ValueConverter<DateOnly?, DateTime?>(
            v => v.HasValue ? v.Value.ToDateTime(TimeOnly.MinValue) : null,
            v => v.HasValue ? DateOnly.FromDateTime(v.Value) : null);

        builder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedOnAdd();
            entity.Property(u => u.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(u => u.ExternalId).IsUnique();
            entity.Property(u => u.Name).HasMaxLength(200).IsRequired();
            entity.Property(u => u.Email).HasMaxLength(320).IsRequired();
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.Role).HasMaxLength(20).IsRequired();
            entity.Property(u => u.AuthProvider).HasMaxLength(32).HasDefaultValue("password");
            entity.Property(u => u.GoogleSub).HasMaxLength(128);
            entity.HasIndex(u => u.GoogleSub);
            entity.Property(u => u.Dni).HasMaxLength(50);
            entity.Property(u => u.Company).HasMaxLength(200);
            entity.Property(u => u.Ruc).HasMaxLength(20);
            entity.Property(u => u.Floor).HasMaxLength(10);
            entity.Property(u => u.Code).HasMaxLength(20);
            entity.Property(u => u.InviteToken).HasMaxLength(64);
            entity.HasIndex(u => u.InviteToken);
            entity.Property(u => u.InviteExpiresAt);
            entity.Property(u => u.InviteUsedAt);
            entity.Property(u => u.InviteEmail).HasMaxLength(320);
            entity.Property(u => u.AdmissionDate).HasConversion(dateOnlyConverter);
            entity.Property(u => u.BillFromNextMonth).HasDefaultValue(false);
            entity.Property(u => u.SubscriptionPlanId).HasMaxLength(20).HasDefaultValue(SubscriptionPlanIds.Free);
            entity.Property(u => u.SubscriptionPaidUntil);
            entity.Property(u => u.AccountStatus).HasMaxLength(20).IsRequired()
                .HasDefaultValue(BuildingFex.Api.Iam.Domain.Model.ValueObjects.AccountStatus.Active);
            entity.Property(u => u.DeactivatedAt);

            entity.HasOne(u => u.OwnerAdmin)
                .WithMany()
                .HasForeignKey(u => u.OwnerAdminId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(u => u.AccountKind).HasMaxLength(20);
            entity.HasOne(u => u.ParentAdmin)
                .WithMany()
                .HasForeignKey(u => u.ParentAdminId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Ignore(u => u.BillingAccount);
            entity.Ignore(u => u.IsManagedBuildingAccount);
        });

        // Miembros de un condominio además del dueño: administradores asignados y junta directiva.
        builder.Entity<BuildingMember>(entity =>
        {
            entity.ToTable("building_members");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedOnAdd();
            entity.Property(m => m.Role).HasMaxLength(20).IsRequired();
            entity.HasIndex(m => new { m.OwnerAdminId, m.UserId }).IsUnique();
            entity.HasIndex(m => m.UserId);
            entity.HasOne(m => m.OwnerAdmin).WithMany().HasForeignKey(m => m.OwnerAdminId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MemberInvitation>(entity =>
        {
            entity.ToTable("member_invitations");
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Id).ValueGeneratedOnAdd();
            entity.Property(i => i.ExternalId).HasMaxLength(64).IsRequired();
            entity.HasIndex(i => i.ExternalId).IsUnique();
            entity.Property(i => i.Email).HasMaxLength(320).IsRequired();
            entity.Property(i => i.Role).HasMaxLength(20).IsRequired();
            entity.Property(i => i.Token).HasMaxLength(64).IsRequired();
            entity.HasIndex(i => i.Token).IsUnique();
            entity.HasIndex(i => i.OwnerAdminId);
            entity.HasOne(i => i.OwnerAdmin).WithMany().HasForeignKey(i => i.OwnerAdminId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
