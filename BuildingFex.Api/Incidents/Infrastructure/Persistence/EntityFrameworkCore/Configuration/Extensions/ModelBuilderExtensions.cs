using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Incidents.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;

public static class ModelBuilderExtensions
{
    public static void ApplyIncidentsConfiguration(this ModelBuilder builder)
    {
        builder.Entity<Incident>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Id).ValueGeneratedOnAdd();
            entity.Property(i => i.ExternalId).HasMaxLength(64).IsRequired();
            entity.HasIndex(i => i.ExternalId).IsUnique();
            entity.Property(i => i.ResidentExternalId).HasMaxLength(64);
            entity.Property(i => i.ResidentName).HasMaxLength(200);
            entity.Property(i => i.Title).HasMaxLength(Incident.TitleMaxLength).IsRequired();
            entity.Property(i => i.Description).HasMaxLength(Incident.DescriptionMaxLength).IsRequired();
            entity.Property(i => i.Category).HasMaxLength(50);
            entity.Property(i => i.Priority).HasMaxLength(20);
            entity.Property(i => i.Status).HasMaxLength(20).IsRequired();
            entity.Property(i => i.Provider).HasMaxLength(200);
            entity.Property(i => i.AssigneeId).HasMaxLength(128);
            entity.Property(i => i.ResolutionComment).HasMaxLength(Incident.CommentMaxLength);
            entity.Property(i => i.ResolvedAt);
            entity.Property(i => i.SourceChatId).HasMaxLength(128);

            entity.HasOne(i => i.OwnerAdmin)
                .WithMany()
                .HasForeignKey(i => i.OwnerAdminId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(i => i.Events)
                .WithOne()
                .HasForeignKey(e => e.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(i => i.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        builder.Entity<IncidentEvent>(entity =>
        {
            entity.ToTable("incident_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Type).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.Comment).HasMaxLength(Incident.CommentMaxLength);
            entity.Property(e => e.AuthorId).HasMaxLength(128);
            entity.Property(e => e.AuthorName).HasMaxLength(200);
            entity.Property(e => e.AuthorRole).HasMaxLength(20);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.IncidentId);
        });
    }
}
