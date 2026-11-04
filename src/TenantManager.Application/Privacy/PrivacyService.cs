using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Application.Common;
using TenantManager.Application.Settings;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Privacy;

public sealed record PrivacyTenantDto(Guid Id, string Name, string Email, bool IsDeleted, DateTimeOffset? AnonymisedAt,
    DateOnly? LastLeaseEndedOn, DateOnly? ErasableFrom, IReadOnlyList<string> AllowedActions);

public sealed record AnonymiseResult(Guid TenantId, DateTimeOffset AnonymisedAt, IReadOnlyList<string> Kept);

/// <summary>
/// POPIA data subject requests (ADR-0020): a tenant (or an Admin for them) can download everything held
/// about them; an Admin can erase a former tenant's personal details once the law allows. What may be
/// erased when, and what must be kept, are legal questions: the retention period is a setting marked
/// LEGAL REVIEW NEEDED and financial and lease records are kept, without the person's contact details.
/// </summary>
public sealed class PrivacyService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    IFileStorage files,
    SettingsService settings,
    ITenantObligationsProvider obligations,
    IUserAccountService accounts,
    AuditRedaction redaction)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Admin: tenants (including deleted ones) with what can be done for each.</summary>
    public async Task<IReadOnlyList<PrivacyTenantDto>> ListAsync(string? search, CancellationToken ct)
    {
        RequireAdmin();
        var q = db.Tenants.IgnoreQueryFilters().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(t => t.FirstName.Contains(term) || t.LastName.Contains(term) || t.Email.Contains(term));
        }
        var tenants = await q.OrderBy(t => t.LastName).Take(100).ToListAsync(ct);
        var years = await settings.GetRetentionYearsAsync(ct);
        var result = new List<PrivacyTenantDto>();
        foreach (var t in tenants)
        {
            var lastEnd = await LastLeaseEndAsync(t.Id, ct);
            var erasableFrom = ErasableFrom(lastEnd, years);
            var actions = new List<string> { "export" };
            if (t.AnonymisedAt is null && await CanEraseAsync(t, erasableFrom, ct) is null) actions.Add("anonymise");
            var running = lastEnd == DateOnly.MaxValue; // shown as no end date yet
            result.Add(new PrivacyTenantDto(t.Id, t.FullName, t.Email, t.IsDeleted, t.AnonymisedAt,
                running ? null : lastEnd, running ? null : erasableFrom, actions));
        }
        return result;
    }

    /// <summary>A zip with tenant.json (every record about the tenant) and the files they gave or were given.</summary>
    public async Task<(byte[] Zip, string FileName)> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await AccessibleTenantAsync(tenantId, ct);
        var login = await accounts.FindTenantLoginAsync(tenant.Id, ct);
        var loginId = login?.UserId;

        var leases = await db.Leases.IgnoreQueryFilters().AsNoTracking().Where(l => l.TenantId == tenant.Id)
            .Include(l => l.Signatures).Include(l => l.Documents).Include(l => l.Unit).ThenInclude(u => u.Property)
            .OrderBy(l => l.StartDate).ToListAsync(ct);
        var leaseIds = leases.Select(l => l.Id).ToList();
        var leaseNumbers = leases.Select(l => l.LeaseNumber).Distinct().ToList();
        var payments = await db.Payments.AsNoTracking().Where(p => p.TenantId == tenant.Id).OrderBy(p => p.PaidOn).ToListAsync(ct);
        var charges = await db.RentCharges.AsNoTracking().Where(c => leaseIds.Contains(c.LeaseId)).OrderBy(c => c.Period).ToListAsync(ct);
        var deposits = await db.Deposits.AsNoTracking().Include(d => d.Deductions)
            .Where(d => leaseNumbers.Contains(d.LeaseNumber)).ToListAsync(ct);
        var issues = await db.Issues.AsNoTracking().Include(i => i.Comments).Include(i => i.Photos)
            .Where(i => i.TenantId == tenant.Id).OrderBy(i => i.OpenedAt).ToListAsync(ct);
        var inspections = await db.Inspections.AsNoTracking().Include(i => i.Items)
            .Where(i => i.TenantId == tenant.Id && i.Status == Domain.Inspections.InspectionStatus.Completed).ToListAsync(ct);
        var consents = await db.Consents.AsNoTracking().Where(c => c.TenantId == tenant.Id).OrderBy(c => c.At).ToListAsync(ct);
        var messages = await db.Messages.AsNoTracking()
            .Where(m => db.Conversations.Any(c => c.Id == m.ConversationId && c.TenantId == tenant.Id))
            .OrderBy(m => m.At).ToListAsync(ct);

        var document = new
        {
            exportedAt = clock.UtcNow,
            note = "Everything Tenant Manager holds about you. The files folder has your leases, proofs of payment, receipts and issue photos.",
            tenant = new { tenant.Id, tenant.FirstName, tenant.LastName, tenant.Email, tenant.Phone, tenant.CreatedAt, tenant.IsDeleted, tenant.AnonymisedAt },
            login = login is null ? null : new { login.Email, login.CreatedAt, login.Locked },
            leases = leases.Select(l => new
            {
                l.LeaseNumber, l.Version, l.Status, Premises = $"{l.Unit.Label}, {l.Unit.Property.Name}", l.StartDate, l.EndDate,
                l.MonthlyRent, l.DepositAmount, l.EscalationPct, l.IsMonthToMonth, l.TerminatesOn, l.TerminationReason,
                Signatures = l.Signatures.Select(s => new { s.Party, s.SignerName, s.SignedAt, s.IpAddress }),
            }),
            rentCharges = charges.Select(c => new { c.Period, c.AmountDue, c.DueDate }),
            payments = payments.Select(p => new
            {
                p.Amount, p.PaidOn, p.Period, p.Reference, p.Status, p.RejectionReason, p.ReceiptNumber, p.FileName, p.CreatedAt,
            }),
            deposits = deposits.Select(d => new
            {
                d.LeaseNumber, d.AmountAgreed, d.AmountReceived, d.ReceivedOn, d.Status, d.AmountRefunded, d.RefundedOn,
                Deductions = d.Deductions.Select(x => new { x.Amount, x.Reason }),
            }),
            issues = issues.Select(i => new
            {
                i.Title, i.Description, i.Category, i.Priority, i.Status, i.OpenedAt, i.ResolvedAt, i.ClosedAt, i.ResolutionNote,
                Comments = i.Comments.OrderBy(c => c.At).Select(c => new { c.AuthorName, c.Body, c.At }),
            }),
            inspections = inspections.Select(i => new
            {
                i.Kind, i.InspectedOn, i.CompletedAt, i.TenantAcknowledgedAt, i.TenantComment,
                Items = i.Items.OrderBy(x => x.Order).Select(x => new { x.Area, x.Condition, x.Notes }),
            }),
            whatsAppConsent = consents.Select(c => new { c.Granted, c.At, c.Source, c.TextVersion, c.Phone, c.Note }),
            messages = messages.Select(m => new { m.Direction, m.Channel, m.Kind, m.TemplateName, m.Body, m.Status, m.At, m.ByName }),
        };

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("tenant.json", CompressionLevel.Optimal);
            await using (var stream = entry.Open())
                await JsonSerializer.SerializeAsync(stream, document, Json, ct);

            foreach (var l in leases)
                foreach (var d in l.Documents)
                    await AddFileAsync(archive, d.BlobKey, $"files/leases/{l.LeaseNumber}-v{l.Version}-{d.Kind}.pdf", ct);
            foreach (var p in payments)
            {
                await AddFileAsync(archive, p.FileKey, $"files/payments/{p.PaidOn:yyyy-MM-dd}-{p.Id:N}-{p.FileName}", ct);
                if (p.ReceiptKey is not null) await AddFileAsync(archive, p.ReceiptKey, $"files/receipts/{p.ReceiptNumber}.pdf", ct);
            }
            foreach (var i in issues)
                foreach (var ph in i.Photos.Where(ph => ph.UploadedBy == loginId))
                    await AddFileAsync(archive, ph.FileKey, $"files/issue-photos/{ph.Id:N}-{ph.FileName}", ct);
        }

        db.AuditLogs.Add(new AuditLog(clock.UtcNow, user.UserId, nameof(Tenant), tenant.Id.ToString(), "Exported", null, null,
            Activity.Current?.TraceId.ToString()));
        await db.SaveChangesAsync(ct);
        return (zip.ToArray(), $"tenant-data-{clock.Today():yyyy-MM-dd}.zip");
    }

    /// <summary>
    /// Admin: erases a former tenant's personal details, keeping the records the business needs
    /// (leases, payments, receipts, the maintenance history) without them. Refused while a lease is in
    /// force, money is owed, or the retention period after the last lease hasn't passed.
    /// </summary>
    public async Task<AnonymiseResult> AnonymiseAsync(Guid tenantId, CancellationToken ct)
    {
        RequireAdmin();
        var tenant = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
                     ?? throw new NotFoundException("Tenant", tenantId);
        if (tenant.AnonymisedAt is not null)
            throw new DomainRuleException("tenant.already_anonymised", "This tenant's details have already been erased.", RuleKind.Conflict);
        var lastEnd = await LastLeaseEndAsync(tenant.Id, ct);
        var erasableFrom = ErasableFrom(lastEnd, await settings.GetRetentionYearsAsync(ct));
        if (await CanEraseAsync(tenant, erasableFrom, ct) is { } refusal) throw refusal;

        var now = clock.UtcNow;
        redaction.Active = true; // the audit log notes what was erased, not the old values
        var loginId = await accounts.AnonymiseTenantLoginAsync(tenant.Id, ct);
        tenant.Anonymise(user.RequireUserId(), now);

        foreach (var c in await db.Consents.Where(c => c.TenantId == tenant.Id).ToListAsync(ct)) c.Redact();
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.TenantId == tenant.Id, ct);
        if (conversation is not null)
        {
            conversation.Redact();
            foreach (var m in await db.Messages.Where(m => m.ConversationId == conversation.Id).ToListAsync(ct)) m.Redact();
        }
        if (loginId is { } uid)
        {
            var issues = await db.Issues.Include(i => i.Comments).Include(i => i.Photos).Include(i => i.Events)
                .Where(i => i.TenantId == tenant.Id).ToListAsync(ct);
            foreach (var i in issues) i.RedactTenant(uid);
            var signatures = await db.Leases.IgnoreQueryFilters().Where(l => l.TenantId == tenant.Id)
                .SelectMany(l => l.Signatures).Where(s => s.UserId == uid).ToListAsync(ct);
            foreach (var s in signatures) s.Redact();
        }
        foreach (var i in await db.Inspections.Where(i => i.TenantId == tenant.Id).ToListAsync(ct)) i.RedactTenantComment();

        await db.SaveChangesAsync(ct);
        redaction.Active = false;
        return new AnonymiseResult(tenant.Id, now,
        [
            "Signed lease PDFs, proofs of payment and receipts (legal and tax records; their contents still name the tenant)",
            "Payment amounts and dates, rent charges and the deposit",
            "The maintenance history of the unit, without the tenant's name or comments",
            "Audit log rows, which are append-only and may hold the old details (LEGAL REVIEW NEEDED: retention of the audit log)",
        ]);
    }

    private async Task<DomainRuleException?> CanEraseAsync(Tenant tenant, DateOnly? erasableFrom, CancellationToken ct)
    {
        var current = await obligations.GetAsync(tenant.Id, ct);
        if (current.HasActiveLease)
            return new DomainRuleException("tenant.has_active_lease", "This tenant has a lease in force.", RuleKind.Conflict);
        if (current.OutstandingBalance > 0)
            return new DomainRuleException("tenant.has_outstanding_balance",
                $"This tenant still owes R{current.OutstandingBalance:N2}.", RuleKind.Conflict);
        if (erasableFrom is { } from && from > clock.Today())
            return new DomainRuleException("tenant.retention_period",
                $"Records must be kept until {from:d MMMM yyyy} (the retention period after the last lease; LEGAL REVIEW NEEDED).",
                RuleKind.Conflict);
        return null;
    }

    private static DateOnly? ErasableFrom(DateOnly? lastEnd, int years) =>
        lastEnd is not { } end ? null : end == DateOnly.MaxValue ? DateOnly.MaxValue : end.AddYears(years);

    /// <summary>The last day of the tenant's last lease that actually ran (MaxValue while one is running).</summary>
    private async Task<DateOnly?> LastLeaseEndAsync(Guid tenantId, CancellationToken ct)
    {
        var ran = await db.Leases.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.ActivatedOn != null)
            .Select(l => new { l.EndDate, l.IsMonthToMonth, l.TerminatesOn, l.Status })
            .ToListAsync(ct);
        if (ran.Count == 0) return null;
        return ran.Max(l => l.Status is LeaseStatus.Active or LeaseStatus.Expiring
            ? DateOnly.MaxValue
            : LeaseTerm.EffectiveEnd(l.EndDate, l.IsMonthToMonth, l.TerminatesOn));
    }

    private async Task<Tenant> AccessibleTenantAsync(Guid tenantId, CancellationToken ct)
    {
        if (user.IsTenant())
        {
            if (user.TenantId != tenantId) throw new NotFoundException("Tenant", tenantId);
        }
        else
        {
            RequireAdmin();
        }
        return await db.Tenants.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
               ?? throw new NotFoundException("Tenant", tenantId);
    }

    private void RequireAdmin()
    {
        if (user.Role != Roles.Admin) throw new UnauthorizedAccessException();
    }

    private async Task AddFileAsync(ZipArchive archive, string key, string name, CancellationToken ct)
    {
        await using var source = await files.OpenReadAsync(key, ct);
        if (source is null) return;
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var target = entry.Open();
        await source.CopyToAsync(target, ct);
    }
}
