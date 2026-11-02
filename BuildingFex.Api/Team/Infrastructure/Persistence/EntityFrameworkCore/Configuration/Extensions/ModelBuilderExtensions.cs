using BuildingFex.Api.Team.Domain.Model.Aggregates;
using BuildingFex.Api.Team.Domain.Model.Entities;
using BuildingFex.Api.Team.Domain.Model.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Team.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;

/// <summary>Team (5.8): team_workers (StaffMember), maintenance_tasks y shifts. Columnas también en SchemaUpgrade.</summary>
public static class ModelBuilderExtensions
{
    public static void ApplyTeamConfiguration(this ModelBuilder builder)
    {
        builder.Entity<TeamWorker>(entity =>
        {
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Id).ValueGeneratedOnAdd();
            entity.Property(w => w.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(w => w.ExternalId).IsUnique();
            entity.Property(w => w.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.Property(w => w.Name).HasMaxLength(256);
            entity.Property(w => w.Phone).HasMaxLength(64);
            entity.Property(w => w.Dni).HasMaxLength(32);
            entity.Property(w => w.Salary).HasPrecision(18, 2);
            entity.Property(w => w.PhotoUrl).HasColumnType("LONGTEXT");
            entity.Property(w => w.Position).HasMaxLength(20).IsRequired().HasDefaultValue(StaffPosition.Default);
            entity.Property(w => w.Status).HasMaxLength(20).IsRequired().HasDefaultValue(StaffStatus.Active);
            entity.Property(w => w.DeactivatedAt);
            entity.Ignore(w => w.IsActive);
        });

        builder.Entity<MaintenanceTask>(entity =>
        {
            entity.ToTable("maintenance_tasks");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).ValueGeneratedOnAdd();
            entity.Property(t => t.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(t => t.ExternalId).IsUnique();
            entity.Property(t => t.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.Property(t => t.StaffId).HasMaxLength(128).IsRequired();
            entity.Property(t => t.IncidentId).HasMaxLength(128);
            entity.Property(t => t.Description).HasMaxLength(MaintenanceTask.DescriptionMaxLength).IsRequired();
            entity.Property(t => t.ScheduledFor);
            entity.Property(t => t.Status).HasMaxLength(20).IsRequired();
            entity.Property(t => t.StartedAt);
            entity.Property(t => t.CompletedAt);
            entity.Property(t => t.CreatedAt);
            entity.Property(t => t.UpdatedAt);
            entity.Ignore(t => t.IsOpen);
            entity.HasIndex(t => new { t.OwnerAdminId, t.StaffId, t.Status }).HasDatabaseName("ix_maintenance_tasks_owner_staff_status");
            entity.HasIndex(t => t.IncidentId).HasDatabaseName("ix_maintenance_tasks_incident_id");
        });

        builder.Entity<Shift>(entity =>
        {
            entity.ToTable("shifts");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedOnAdd();
            entity.Property(s => s.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(s => s.ExternalId).IsUnique();
            entity.Property(s => s.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.Property(s => s.StaffId).HasMaxLength(128).IsRequired();
            entity.Property(s => s.StartsAt).HasColumnName("starts_at");
            entity.Property(s => s.EndsAt).HasColumnName("ends_at");
            entity.Property(s => s.Notes).HasMaxLength(500);
            entity.Property(s => s.CreatedAt);
            entity.HasIndex(s => new { s.OwnerAdminId, s.StaffId, s.StartsAt }).HasDatabaseName("ix_shifts_owner_staff_starts_at");
        });
    }
}
