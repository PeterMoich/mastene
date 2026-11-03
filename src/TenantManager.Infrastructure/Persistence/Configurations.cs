using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Tenants;
using TenantManager.Infrastructure.Identity;

namespace TenantManager.Infrastructure.Persistence;

internal sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> b)
    {
        b.ToTable("Properties");
        b.Property(p => p.Name).HasMaxLength(Property.NameMax).IsRequired();
        b.Property(p => p.AddressLine1).HasMaxLength(Property.AddressLineMax).IsRequired();
        b.Property(p => p.AddressLine2).HasMaxLength(Property.AddressLineMax);
        b.Property(p => p.City).HasMaxLength(Property.CityMax).IsRequired();
        b.Property(p => p.Province).HasMaxLength(40).IsRequired();
        b.Property(p => p.PostalCode).HasMaxLength(4).IsFixedLength().IsRequired();
        b.Ignore(p => p.Address);
        b.HasIndex(p => new { p.OwnerId, p.Name });

        b.HasMany(p => p.Units).WithOne(u => u.Property).HasForeignKey(u => u.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Navigation(p => p.Units).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasQueryFilter(p => !p.IsDeleted);
    }
}

internal sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> b)
    {
        b.ToTable("Units");
        b.Property(u => u.Label).HasMaxLength(Unit.LabelMax).IsRequired();
        b.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);

        // Unique among live units only, so a deleted "Flat 1" doesn't block a new one.
        // SQL Server's default collation is case-insensitive, matching the domain check.
        b.HasIndex(u => new { u.PropertyId, u.Label }).IsUnique().HasFilter("[IsDeleted] = 0");

        b.HasQueryFilter(u => !u.IsDeleted);
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("Tenants");
        b.Property(t => t.FirstName).HasMaxLength(Tenant.NameMax).IsRequired();
        b.Property(t => t.LastName).HasMaxLength(Tenant.NameMax).IsRequired();
        b.Property(t => t.Email).HasMaxLength(Tenant.EmailMax).IsRequired();
        b.Property(t => t.Phone).HasMaxLength(20).IsRequired();
        b.Ignore(t => t.FullName);

        b.HasIndex(t => new { t.OwnerId, t.Email }).IsUnique().HasFilter("[IsDeleted] = 0");
        b.HasIndex(t => new { t.OwnerId, t.LastName });

        b.HasQueryFilter(t => !t.IsDeleted);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.Property(a => a.Id).UseIdentityColumn();
        b.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        b.Property(a => a.EntityId).HasMaxLength(64).IsRequired();
        b.Property(a => a.Action).HasMaxLength(20).IsRequired();
        b.Property(a => a.CorrelationId).HasMaxLength(64);
        b.HasIndex(a => new { a.EntityType, a.EntityId });
        b.HasIndex(a => a.OccurredAt);
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.Property(u => u.DisplayName).HasMaxLength(120).IsRequired();
        b.HasIndex(u => u.TenantId).IsUnique().HasFilter("[TenantId] IS NOT NULL");
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("RefreshTokens");
        b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        b.Property(t => t.ReplacedByTokenHash).HasMaxLength(64);
        b.HasIndex(t => t.TokenHash).IsUnique();
        b.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
