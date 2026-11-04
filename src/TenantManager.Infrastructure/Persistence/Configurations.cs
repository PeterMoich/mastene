using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Settings;
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

internal sealed class LeaseConfiguration : IEntityTypeConfiguration<Lease>
{
    public void Configure(EntityTypeBuilder<Lease> b)
    {
        b.ToTable("Leases");
        b.Property(l => l.LeaseNumber).HasMaxLength(20).IsRequired();
        b.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(l => l.EscalationPct).HasPrecision(5, 2);
        b.Property(l => l.TerminationReason).HasMaxLength(Lease.ReasonMax);
        b.Property(l => l.LandlordName).HasMaxLength(Lease.NameMax);
        b.Property(l => l.TemplateVersion).HasMaxLength(Lease.TemplateVersionMax);
        b.Property(l => l.RowVersion).IsRowVersion();
        b.Ignore(l => l.IsRenewal);
        b.Ignore(l => l.IsCurrent);
        b.Ignore(l => l.IsFullySigned);
        b.Ignore(l => l.EffectiveEndDate);

        b.HasIndex(l => new { l.LeaseNumber, l.Version }).IsUnique();
        b.HasIndex(l => new { l.UnitId, l.Status });
        b.HasIndex(l => new { l.TenantId, l.Status });
        b.HasIndex(l => new { l.OwnerId, l.StartDate });
        // A lease has at most one renewal that hasn't been cancelled.
        b.HasIndex(l => l.PreviousLeaseId).IsUnique().HasFilter("[PreviousLeaseId] IS NOT NULL AND [Status] <> N'Terminated'");

        b.HasOne(l => l.Unit).WithMany().HasForeignKey(l => l.UnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(l => l.Tenant).WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(l => l.Previous).WithMany().HasForeignKey(l => l.PreviousLeaseId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(l => l.Signatures).WithOne().HasForeignKey(s => s.LeaseId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(l => l.Documents).WithOne().HasForeignKey(d => d.LeaseId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(l => l.ExpiryNotices).WithOne().HasForeignKey(n => n.LeaseId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(l => l.Signatures).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(l => l.Documents).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(l => l.ExpiryNotices).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class LeaseSignatureConfiguration : IEntityTypeConfiguration<LeaseSignature>
{
    public void Configure(EntityTypeBuilder<LeaseSignature> b)
    {
        b.ToTable("LeaseSignatures");
        b.Property(s => s.Party).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.SignerName).HasMaxLength(Lease.NameMax).IsRequired();
        b.Property(s => s.IpAddress).HasMaxLength(45);
        b.HasIndex(s => new { s.LeaseId, s.Party }).IsUnique(); // each party signs once
    }
}

internal sealed class LeaseDocumentConfiguration : IEntityTypeConfiguration<LeaseDocument>
{
    public void Configure(EntityTypeBuilder<LeaseDocument> b)
    {
        b.ToTable("LeaseDocuments");
        b.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.BlobKey).HasMaxLength(200).IsRequired();
        b.Property(d => d.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        b.HasIndex(d => d.BlobKey).IsUnique();
    }
}

internal sealed class LeaseExpiryNoticeConfiguration : IEntityTypeConfiguration<LeaseExpiryNotice>
{
    public void Configure(EntityTypeBuilder<LeaseExpiryNotice> b)
    {
        b.ToTable("LeaseExpiryNotices");
        b.HasIndex(n => new { n.LeaseId, n.DaysBefore }).IsUnique(); // BR05: each notice once
    }
}

internal sealed class BusinessSettingsConfiguration : IEntityTypeConfiguration<BusinessSettings>
{
    public void Configure(EntityTypeBuilder<BusinessSettings> b)
    {
        b.ToTable("Settings");
        b.Property(s => s.ExpiryNoticeDays).HasMaxLength(50).IsRequired();
        b.Property(s => s.DefaultEscalationPct).HasPrecision(5, 2);
        b.Property(s => s.MaxEscalationPct).HasPrecision(5, 2);
        b.Property(s => s.LeaseTemplateVersion).HasMaxLength(Lease.TemplateVersionMax).IsRequired();
    }
}
