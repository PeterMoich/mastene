using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Inspections;
using TenantManager.Domain.Issues;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Messaging;
using TenantManager.Domain.Payments;
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

internal sealed class RentChargeConfiguration : IEntityTypeConfiguration<RentCharge>
{
    public void Configure(EntityTypeBuilder<RentCharge> b)
    {
        b.ToTable("RentCharges");
        b.Property(c => c.Period).HasMaxLength(7).IsFixedLength().IsRequired();
        b.Ignore(c => c.BillingPeriod);
        b.HasIndex(c => new { c.LeaseId, c.Period }).IsUnique(); // one charge per lease per month
        b.HasIndex(c => new { c.OverdueNoticeOn, c.DueDate });
        b.HasOne<Lease>().WithMany().HasForeignKey(c => c.LeaseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProofOfPaymentConfiguration : IEntityTypeConfiguration<ProofOfPayment>
{
    public void Configure(EntityTypeBuilder<ProofOfPayment> b)
    {
        b.ToTable("Payments");
        b.Property(p => p.Period).HasMaxLength(7).IsFixedLength();
        b.Property(p => p.Reference).HasMaxLength(ProofOfPayment.ReferenceMax);
        b.Property(p => p.FileKey).HasMaxLength(200).IsRequired();
        b.Property(p => p.FileName).HasMaxLength(ProofOfPayment.FileNameMax).IsRequired();
        b.Property(p => p.ContentType).HasMaxLength(50).IsRequired();
        b.Property(p => p.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.RejectionReason).HasMaxLength(ProofOfPayment.ReasonMax);
        b.Property(p => p.ReviewedByName).HasMaxLength(120);
        b.Property(p => p.ReceiptNumber).HasMaxLength(20);
        b.Property(p => p.ReceiptKey).HasMaxLength(200);
        b.Property(p => p.RowVersion).IsRowVersion();

        // BR10: the same file can't be submitted twice for a lease, unless the earlier one was rejected.
        b.HasIndex(p => new { p.LeaseId, p.Sha256 }).IsUnique().HasFilter("[Status] <> N'Rejected'");
        b.HasIndex(p => new { p.OwnerId, p.Status, p.CreatedAt });
        b.HasIndex(p => new { p.TenantId, p.CreatedAt });
        b.HasIndex(p => p.ReceiptNumber).IsUnique().HasFilter("[ReceiptNumber] IS NOT NULL");
        b.HasOne<Lease>().WithMany().HasForeignKey(p => p.LeaseId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DepositConfiguration : IEntityTypeConfiguration<Deposit>
{
    public void Configure(EntityTypeBuilder<Deposit> b)
    {
        b.ToTable("Deposits");
        b.Property(d => d.LeaseNumber).HasMaxLength(20).IsRequired();
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        b.Ignore(d => d.TotalDeducted);
        b.Ignore(d => d.Remaining);
        b.HasIndex(d => d.LeaseNumber).IsUnique(); // one deposit per lease, shared by its renewals
        b.HasMany(d => d.Deductions).WithOne().HasForeignKey(x => x.DepositId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(d => d.Deductions).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class DepositDeductionConfiguration : IEntityTypeConfiguration<DepositDeduction>
{
    public void Configure(EntityTypeBuilder<DepositDeduction> b)
    {
        b.ToTable("DepositDeductions");
        b.Property(x => x.Reason).HasMaxLength(Deposit.ReasonMax).IsRequired();
    }
}

internal sealed class IssueConfiguration : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> b)
    {
        b.ToTable("Issues");
        b.Property(i => i.Title).HasMaxLength(Issue.TitleMax).IsRequired();
        b.Property(i => i.Description).HasMaxLength(Issue.DescriptionMax).IsRequired();
        b.Property(i => i.Category).HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.Priority).HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.ReportedByName).HasMaxLength(120).IsRequired();
        b.Property(i => i.ResolutionNote).HasMaxLength(Issue.NoteMax);
        b.Property(i => i.RowVersion).IsRowVersion();
        b.Ignore(i => i.IsFinished);

        b.HasIndex(i => new { i.OwnerId, i.Status, i.OpenedAt });
        b.HasIndex(i => new { i.TenantId, i.OpenedAt });
        b.HasIndex(i => i.UnitId);
        b.HasIndex(i => i.ContractorId);
        // The SLA job looks for issues that aren't closed and are past a target.
        b.HasIndex(i => new { i.Status, i.ResponseDueAt, i.ResolutionDueAt });

        b.HasOne<Unit>().WithMany().HasForeignKey(i => i.UnitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lease>().WithMany().HasForeignKey(i => i.LeaseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Contractor>().WithMany().HasForeignKey(i => i.ContractorId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(i => i.Comments).WithOne().HasForeignKey(c => c.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(i => i.Photos).WithOne().HasForeignKey(p => p.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(i => i.Events).WithOne().HasForeignKey(e => e.IssueId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(i => i.Comments).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(i => i.Photos).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(i => i.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class IssueCommentConfiguration : IEntityTypeConfiguration<IssueComment>
{
    public void Configure(EntityTypeBuilder<IssueComment> b)
    {
        b.ToTable("IssueComments");
        b.Property(c => c.AuthorName).HasMaxLength(120).IsRequired();
        b.Property(c => c.Body).HasMaxLength(IssueComment.BodyMax).IsRequired();
        b.HasIndex(c => new { c.IssueId, c.At });
    }
}

internal sealed class IssuePhotoConfiguration : IEntityTypeConfiguration<IssuePhoto>
{
    public void Configure(EntityTypeBuilder<IssuePhoto> b)
    {
        b.ToTable("IssuePhotos");
        b.Property(p => p.FileKey).HasMaxLength(200).IsRequired();
        b.Property(p => p.FileName).HasMaxLength(120).IsRequired();
        b.Property(p => p.ContentType).HasMaxLength(50).IsRequired();
        b.Property(p => p.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(p => p.UploadedByName).HasMaxLength(120).IsRequired();
        b.HasIndex(p => new { p.IssueId, p.Sha256 }).IsUnique(); // each photo once per issue
        b.HasIndex(p => p.FileKey).IsUnique();
    }
}

internal sealed class IssueEventConfiguration : IEntityTypeConfiguration<IssueEvent>
{
    public void Configure(EntityTypeBuilder<IssueEvent> b)
    {
        b.ToTable("IssueEvents");
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(30);
        b.Property(e => e.ByName).HasMaxLength(120).IsRequired();
        b.Property(e => e.Note).HasMaxLength(Issue.NoteMax);
        b.HasIndex(e => new { e.IssueId, e.At });
    }
}

internal sealed class ContractorConfiguration : IEntityTypeConfiguration<Contractor>
{
    public void Configure(EntityTypeBuilder<Contractor> b)
    {
        b.ToTable("Contractors");
        b.Property(c => c.Name).HasMaxLength(Contractor.NameMax).IsRequired();
        b.Property(c => c.Trade).HasMaxLength(Contractor.TradeMax).IsRequired();
        b.Property(c => c.Phone).HasMaxLength(20).IsRequired();
        b.Property(c => c.Email).HasMaxLength(Contractor.EmailMax);
        b.Property(c => c.Notes).HasMaxLength(Contractor.NotesMax);
        b.HasIndex(c => new { c.OwnerId, c.Name });
        b.HasQueryFilter(c => !c.IsDeleted);
    }
}

internal sealed class InspectionConfiguration : IEntityTypeConfiguration<Inspection>
{
    public void Configure(EntityTypeBuilder<Inspection> b)
    {
        b.ToTable("Inspections");
        b.Property(i => i.LeaseNumber).HasMaxLength(20).IsRequired();
        b.Property(i => i.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.GeneralNotes).HasMaxLength(Inspection.NotesMax);
        b.Property(i => i.CompletedByName).HasMaxLength(120);
        b.Property(i => i.TenantComment).HasMaxLength(Inspection.CommentMax);
        b.Property(i => i.RowVersion).IsRowVersion();

        b.HasIndex(i => new { i.LeaseNumber, i.Kind }).IsUnique(); // one move-in and one move-out per lease
        b.HasIndex(i => new { i.TenantId, i.Status });
        b.HasOne<Lease>().WithMany().HasForeignKey(i => i.LeaseId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(i => i.Items).WithOne().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(i => i.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class InspectionItemConfiguration : IEntityTypeConfiguration<InspectionItem>
{
    public void Configure(EntityTypeBuilder<InspectionItem> b)
    {
        b.ToTable("InspectionItems");
        b.Property(x => x.Area).HasMaxLength(Inspection.AreaMax).IsRequired();
        b.Property(x => x.Condition).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(Inspection.NotesMax);
        b.HasIndex(x => new { x.InspectionId, x.Area }).IsUnique();
        b.HasMany(x => x.Photos).WithOne().HasForeignKey(p => p.InspectionItemId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Photos).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class InspectionPhotoConfiguration : IEntityTypeConfiguration<InspectionPhoto>
{
    public void Configure(EntityTypeBuilder<InspectionPhoto> b)
    {
        b.ToTable("InspectionPhotos");
        b.Property(p => p.FileKey).HasMaxLength(200).IsRequired();
        b.Property(p => p.FileName).HasMaxLength(120).IsRequired();
        b.Property(p => p.ContentType).HasMaxLength(50).IsRequired();
        b.Property(p => p.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        b.HasIndex(p => p.FileKey).IsUnique();
    }
}

internal sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> b)
    {
        b.ToTable("Consents");
        b.Property(c => c.Channel).HasMaxLength(20).IsRequired();
        b.Property(c => c.Phone).HasMaxLength(20).IsRequired();
        b.Property(c => c.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(c => c.TextVersion).HasMaxLength(30).IsRequired();
        b.Property(c => c.RecordedByName).HasMaxLength(120).IsRequired();
        b.Property(c => c.Note).HasMaxLength(ConsentRecord.NoteMax);
        b.HasIndex(c => new { c.TenantId, c.At });
        b.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("Conversations");
        b.Property(c => c.LastMessagePreview).HasMaxLength(Conversation.PreviewMax);
        b.Property(c => c.RowVersion).IsRowVersion();
        b.Ignore(c => c.ServiceWindowEndsAt);
        b.HasIndex(c => c.TenantId).IsUnique(); // one thread per tenant
        b.HasIndex(c => new { c.OwnerId, c.LastMessageAt });
        b.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.ToTable("Messages");
        b.Property(m => m.Direction).HasConversion<string>().HasMaxLength(10);
        b.Property(m => m.Channel).HasConversion<string>().HasMaxLength(10);
        b.Property(m => m.Kind).HasConversion<string>().HasMaxLength(10);
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(m => m.Body).HasMaxLength(Message.BodyMax).IsRequired();
        b.Property(m => m.TemplateName).HasMaxLength(60);
        b.Property(m => m.TemplateParameters).HasMaxLength(2000);
        b.Property(m => m.ProviderMessageId).HasMaxLength(128);
        b.Property(m => m.DedupeKey).HasMaxLength(100);
        b.Property(m => m.Error).HasMaxLength(Message.ErrorMax);
        b.Property(m => m.ByName).HasMaxLength(120);
        b.Property(m => m.RowVersion).IsRowVersion();
        b.Ignore(m => m.Parameters);

        b.HasIndex(m => new { m.ConversationId, m.At });
        // A webhook delivered twice stores one message; a reminder is queued once.
        b.HasIndex(m => m.ProviderMessageId).IsUnique().HasFilter("[ProviderMessageId] IS NOT NULL");
        b.HasIndex(m => m.DedupeKey).IsUnique().HasFilter("[DedupeKey] IS NOT NULL");
        b.HasIndex(m => new { m.Status, m.Channel, m.At }); // the dispatcher's queue
        b.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
