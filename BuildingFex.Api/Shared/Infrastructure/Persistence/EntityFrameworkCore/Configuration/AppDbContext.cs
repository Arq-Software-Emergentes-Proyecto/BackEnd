using BuildingFex.Api.Finances.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;
using BuildingFex.Api.Iam.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;
using BuildingFex.Api.Incidents.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;
using BuildingFex.Api.Communications.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;
using BuildingFex.Api.SocialSpaces.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;
using BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;
using BuildingFex.Api.ResidentCare.Domain.Model.Aggregates;
using BuildingFex.Api.Communications.Domain.Model.Aggregates;
using BuildingFex.Api.Incidents.Domain.Model.Aggregates;
using BuildingFex.Api.Finances.Domain.Model.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Shared.Infrastructure.Persistence.EntityFrameworkCore.Configuration;

/// <summary>
/// DbContext compartido por todos los bounded contexts de BuildingFex.
/// Cada módulo registra sus tablas en OnModelCreating via Apply*Configuration().
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyIamConfiguration();
        builder.ApplyIncidentsConfiguration();
        builder.ApplyFinancesConfiguration();
        builder.ApplySocialSpacesConfiguration();
        builder.ApplyCommunicationsConfiguration();
        // Billing (subscriptions) + Profiles (buildings)
        BuildingFex.Api.Billing.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions
            .ModelBuilderExtensions.ApplyBillingConfiguration(builder);
        BuildingFex.Api.Profiles.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions
            .ModelBuilderExtensions.ApplyProfilesConfiguration(builder);

        // ResidentCare (Support) — support_chats + support_messages (sin blob messages_json)
        builder.Entity<SupportChat>(entity =>
        {
            entity.ToTable("support_chats");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedOnAdd();
            entity.Property(s => s.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(s => s.ExternalId).IsUnique();
            entity.Property(s => s.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.HasIndex(s => s.OwnerAdminId);
            entity.Property(s => s.ResidentId).HasMaxLength(128).IsRequired();
            entity.HasIndex(s => s.ResidentId);
            entity.Property(s => s.ResidentName).HasMaxLength(256);
            entity.Property(s => s.Topic).HasMaxLength(256);
            entity.Property(s => s.Status).HasMaxLength(32);
            entity.Property(s => s.Kind).HasMaxLength(16).IsRequired();
            entity.HasIndex(s => s.Kind);
            entity.Property(s => s.Source).HasMaxLength(32);
            entity.Property(s => s.LinkedChatId).HasMaxLength(128);
            entity.Property(s => s.IncidentExternalId).HasMaxLength(64);
            entity.Property(s => s.TitleSummarized);
            entity.Property(s => s.ClosedAt);
            entity.HasIndex(s => new { s.OwnerAdminId, s.Kind, s.Status });
            entity.HasMany(s => s.Messages)
                .WithOne(m => m.Chat!)
                .HasForeignKey(m => m.ChatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SupportMessage>(entity =>
        {
            entity.ToTable("support_messages");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).ValueGeneratedOnAdd();
            entity.Property(m => m.ExternalId).HasMaxLength(64).IsRequired();
            entity.HasIndex(m => m.ExternalId).IsUnique();
            entity.Property(m => m.AuthorRole).HasMaxLength(32).IsRequired();
            entity.Property(m => m.Body).HasColumnType("LONGTEXT").IsRequired();
            entity.Property(m => m.AuthorName).HasMaxLength(256);
            entity.Property(m => m.CitedDocumentId).HasMaxLength(128);
            entity.Property(m => m.CitedSource).HasMaxLength(512);
            entity.HasIndex(m => m.ChatId);
            entity.HasIndex(m => m.CreatedAt);
        });

        builder.Entity<ImportUpload>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedOnAdd();
            entity.Property(u => u.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(u => u.ExternalId).IsUnique();
            entity.Property(u => u.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.HasIndex(u => u.OwnerAdminId);
            entity.Property(u => u.FileName).HasMaxLength(512);
            entity.Property(u => u.MimeType).HasMaxLength(128);
            entity.Property(u => u.StoragePath).HasMaxLength(1024);
            entity.Property(u => u.DownloadUrl).HasColumnType("LONGTEXT"); // legacy, no se escribe
            entity.Property(u => u.DataUrl).HasColumnType("LONGTEXT"); // legacy
            entity.Property(u => u.FolderId).HasMaxLength(128);
            entity.Property(u => u.UploadStatus).HasMaxLength(32).IsRequired();
            entity.HasIndex(u => u.UploadStatus);
            // OfficialDocument (Tabla 76): tipo, versión y vigencia. También en SchemaUpgrade.ApplyOfficialDocumentsAsync.
            entity.Property(u => u.DocumentType).HasMaxLength(20).IsRequired().HasDefaultValue(OfficialDocumentType.Other);
            entity.Property(u => u.Version).IsRequired().HasDefaultValue(1);
            entity.Property(u => u.PublishedAt);
            entity.Property(u => u.DocumentStatus).HasMaxLength(20).IsRequired().HasDefaultValue(OfficialDocumentStatus.Published);
            entity.Property(u => u.SupersedesId).HasMaxLength(128);
            entity.HasIndex(u => new { u.OwnerAdminId, u.DocumentStatus }).HasDatabaseName("ix_import_uploads_owner_document_status");
            entity.Property(u => u.IndexingStatus).HasMaxLength(32).IsRequired().HasDefaultValue(ImportIndexingStatus.None);
            entity.HasIndex(u => u.IndexingStatus);
            entity.Property(u => u.IndexingError).HasMaxLength(1024);
        });

        builder.Entity<ImportFolder>(entity =>
        {
            entity.ToTable("import_folders");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Id).ValueGeneratedOnAdd();
            entity.Property(f => f.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(f => f.ExternalId).IsUnique();
            entity.Property(f => f.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.Property(f => f.Name).HasMaxLength(256);
            entity.Property(f => f.IsHome);
            entity.Property(f => f.IsContext);
        });

        // ResidentCare: tope mensual de consultas IA por cuenta (CON11)
        builder.Entity<AiUsage>(entity =>
        {
            entity.ToTable("ai_usages");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedOnAdd();
            entity.Property(u => u.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.Property(u => u.Period).HasMaxLength(7).IsRequired();
            entity.Property(u => u.QueryCount).IsRequired();
            entity.HasIndex(u => new { u.OwnerAdminId, u.Period }).IsUnique();
        });

        builder.Entity<AiContextChunk>(entity =>
        {
            entity.ToTable("ai_context_chunks");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();
            entity.Property(c => c.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.HasIndex(c => c.OwnerAdminId).HasDatabaseName("ix_ai_context_chunks_owner");
            entity.Property(c => c.UploadExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(c => c.UploadExternalId).HasDatabaseName("ix_ai_context_chunks_upload");
            entity.Property(c => c.FileName).HasMaxLength(512);
            entity.Property(c => c.Content).HasColumnType("LONGTEXT").IsRequired();
            entity.Property(c => c.EmbeddingJson).HasColumnType("LONGTEXT").IsRequired();
            entity.Property(c => c.EmbeddingModel).HasMaxLength(64).IsRequired().HasDefaultValue(AiContextChunk.LegacyEmbeddingModel);
        });

        // Team (5.8): team_workers + maintenance_tasks + shifts
        BuildingFex.Api.Team.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions
            .ModelBuilderExtensions.ApplyTeamConfiguration(builder);

        builder.Entity<BuildingServiceProvider>(entity =>
        {
            entity.ToTable("service_providers");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedOnAdd();
            entity.Property(p => p.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(p => p.ExternalId).IsUnique();
            entity.Property(p => p.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.Property(p => p.Name).HasMaxLength(256);
            entity.Property(p => p.Phone).HasMaxLength(64);
            entity.Property(p => p.Specialty).HasMaxLength(128);
        });

        builder.Entity<ResidentExtraCharge>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedOnAdd();
            entity.Property(c => c.ExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(c => c.ExternalId).IsUnique();
            entity.Property(c => c.OwnerAdminId).HasMaxLength(128).IsRequired();
            entity.Property(c => c.ResidentId).HasMaxLength(128).IsRequired();
            entity.Property(c => c.Title).HasMaxLength(256);
            entity.Property(c => c.Amount).HasPrecision(18, 2);
            entity.Property(c => c.Description).HasColumnType("LONGTEXT");
        });

        builder.Entity<BuildingFex.Api.Profiles.Domain.Model.Aggregates.Profile>(entity =>
        {
            entity.ToTable("profiles");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedOnAdd();
            entity.Property(p => p.UserExternalId).HasMaxLength(128).IsRequired();
            entity.HasIndex(p => p.UserExternalId).IsUnique();
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();
            entity.Property(p => p.Email).HasMaxLength(320).IsRequired();
            entity.Property(p => p.Dni).HasMaxLength(50);
            entity.Property(p => p.Address).HasMaxLength(500);
            entity.Property(p => p.Company).HasMaxLength(200);
            entity.Property(p => p.Ruc).HasMaxLength(20);

            // FK real 1:1: profiles.user_external_id → users.external_id
            entity.HasOne<BuildingFex.Api.Iam.Domain.Model.Aggregates.User>()
                .WithOne()
                .HasForeignKey<BuildingFex.Api.Profiles.Domain.Model.Aggregates.Profile>(p => p.UserExternalId)
                .HasPrincipalKey<BuildingFex.Api.Iam.Domain.Model.Aggregates.User>(u => u.ExternalId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.UseSnakeCaseNamingConvention();
    }
}
