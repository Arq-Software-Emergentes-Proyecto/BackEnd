using BuildingFex.Api.Iam.Domain.Model.Aggregates;
using BuildingFex.Api.Profiles.Domain.Model.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace BuildingFex.Api.Profiles.Infrastructure.Persistence.EntityFrameworkCore.Configuration.Extensions;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Tabla buildings (también en SchemaUpgrade). El agregado Profile sigue configurado en AppDbContext.
    /// </summary>
    public static void ApplyProfilesConfiguration(this ModelBuilder builder)
    {
        builder.Entity<Building>(entity =>
        {
            entity.ToTable("buildings");
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Id).ValueGeneratedOnAdd();
            entity.Property(b => b.OwnerAdminId).IsRequired();
            entity.HasIndex(b => b.OwnerAdminId);
            entity.Property(b => b.Name).HasMaxLength(Building.NameMaxLength).IsRequired();
            entity.Property(b => b.Address).HasMaxLength(Building.AddressMaxLength).IsRequired();
            entity.Property(b => b.DepartmentCount).IsRequired();

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(b => b.OwnerAdminId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
