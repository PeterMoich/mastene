using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Application.Files;
using TenantManager.Application.Leases;
using TenantManager.Application.Messaging;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;

namespace TenantManager.Application.Payments;

/// <param name="Period">Optional "yyyy-MM" month this payment is for.</param>
public sealed record SubmitPaymentRequest(Guid LeaseId, decimal Amount, DateOnly PaidOn, string? Period, string? Reference);

public sealed record RejectPaymentRequest(string Reason);

public sealed record PaymentListQuery : PagedQuery
{
    public PaymentStatus? Status { get; init; }
    public Guid? LeaseId { get; init; }
    public Guid? PropertyId { get; init; }

    /// <summary>"yyyy-MM".</summary>
    public string? Period { get; init; }

    /// <summary>Paid on or after this date.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Paid on or before this date.</summary>
    public DateOnly? To { get; init; }
}

/// <param name="AllowedActions">verify, reject, viewFile, downloadReceipt — for the current user.</param>
public sealed record PaymentDto(
    Guid Id, Guid LeaseId, string LeaseNumber, int LeaseVersion, Guid TenantId, string TenantName,
    string PropertyName, string UnitLabel, decimal Amount, DateOnly PaidOn, string? Period, string? Reference,
    string FileName, string ContentType, long SizeBytes, PaymentStatus Status, string? RejectionReason,
    string? ReviewedByName, DateTimeOffset? ReviewedAt, string? ReceiptNumber, DateTimeOffset CreatedAt,
    IReadOnlyList<string> AllowedActions);

public sealed class SubmitPaymentRequestValidator : AbstractValidator<SubmitPaymentRequest>
{
    public SubmitPaymentRequestValidator()
    {
        RuleFor(x => x.LeaseId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0).LessThanOrEqualTo(1_000_000).PrecisionScale(18, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.PaidOn).InclusiveBetween(new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31))
            .WithMessage("Enter the date you paid.");
        RuleFor(x => x.Period).Matches(@"^\d{4}-(0[1-9]|1[0-2])$").When(x => !string.IsNullOrEmpty(x.Period))
            .WithMessage("Period must look like 2026-03.");
        RuleFor(x => x.Reference).MaximumLength(ProofOfPayment.ReferenceMax);
    }
}

public sealed class RejectPaymentRequestValidator : AbstractValidator<RejectPaymentRequest>
{
    public RejectPaymentRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(ProofOfPayment.ReasonMax);
}

/// <summary>
/// Proof of payment: tenants (or the landlord on their behalf) upload it, the landlord verifies or
/// rejects it (BR10, BR11). Verifying issues a receipt PDF.
/// </summary>
public sealed class PaymentService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    UploadValidator uploads,
    IFileStorage files,
    ReceiptFactory receipts,
    Notifier notifier,
    MessageDispatcher dispatcher,
    IValidator<SubmitPaymentRequest> submitValidator,
    IValidator<RejectPaymentRequest> rejectValidator)
{
    private static readonly Dictionary<string, Expression<Func<ProofOfPayment, object>>> Sorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["paidOn"] = p => p.PaidOn,
            ["createdAt"] = p => p.CreatedAt,
            ["amount"] = p => p.Amount,
            ["status"] = p => p.Status,
        };

    private IQueryable<ProofOfPayment> Visible()
    {
        if (user.IsTenant())
        {
            var tenantId = user.TenantId ?? Guid.Empty;
            return db.Payments.Where(p => p.TenantId == tenantId);
        }
        var ownerId = user.RequireOwnerId();
        return db.Payments.Where(p => p.OwnerId == ownerId);
    }

    public async Task<PagedResult<PaymentDto>> ListAsync(PaymentListQuery query, CancellationToken ct)
    {
        var q = Visible();
        if (query.Status is { } status) q = q.Where(p => p.Status == status);
        if (query.LeaseId is { } leaseId) q = q.Where(p => p.LeaseId == leaseId);
        if (!string.IsNullOrWhiteSpace(query.Period)) q = q.Where(p => p.Period == query.Period);
        if (query.From is { } from) q = q.Where(p => p.PaidOn >= from);
        if (query.To is { } to) q = q.Where(p => p.PaidOn <= to);
        if (query.PropertyId is { } propertyId)
            q = q.Where(p => db.Leases.IgnoreQueryFilters().Any(l => l.Id == p.LeaseId && l.Unit.PropertyId == propertyId));

        var page = await q.ApplySort(query.Sort, Sorts, "-createdAt").ToPagedAsync(query, p => p, ct);
        return page.MapItems(await ToDtosAsync(page.Items, ct));
    }

    public async Task<PaymentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var payment = await Visible().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
                      ?? throw new NotFoundException("Payment", id);
        return (await ToDtosAsync([payment], ct))[0];
    }

    public async Task<PaymentDto> SubmitAsync(SubmitPaymentRequest request, UploadedFile file, CancellationToken ct)
    {
        await submitValidator.ValidateAndThrowAsync(request, ct);
        var lease = await db.VisibleLeases(user).FirstOrDefaultAsync(l => l.Id == request.LeaseId, ct)
                    ?? throw new NotFoundException("Lease", request.LeaseId);
        if (!LeaseAccess.AcceptsPayments(lease))
            throw new DomainRuleException("lease.not_payable", "Payments can only be recorded for a lease that has started.",
                RuleKind.Conflict);

        BillingPeriod? period = null;
        if (!string.IsNullOrEmpty(request.Period))
        {
            period = BillingPeriod.Parse(request.Period);
            var last = BillingPeriod.Of(lease.EffectiveEndDate == DateOnly.MaxValue ? clock.Today().AddMonths(1) : lease.EffectiveEndDate);
            if (period < BillingPeriod.Of(lease.StartDate) || period > last)
                throw new DomainRuleException("period.outside_lease", "That month is outside this lease's term.");
        }

        var upload = await uploads.ValidateAsync(file, UploadRules.ProofOfPayment, ct);

        // BR10: the same file can't be submitted twice for a lease (a rejected one may be sent again).
        if (await db.Payments.AnyAsync(p => p.LeaseId == lease.Id && p.Sha256 == upload.Sha256 &&
                                            p.Status != PaymentStatus.Rejected, ct))
            throw new DomainRuleException("payment.duplicate", "This exact file has already been submitted for this lease.",
                RuleKind.Conflict);

        var key = $"payments/{lease.Id:N}/{Guid.NewGuid():N}{upload.Extension}";
        using (var stream = new MemoryStream(upload.Content, writable: false))
        {
            await files.SaveAsync(key, stream, upload.ContentType, ct);
        }

        var payment = new ProofOfPayment(lease.OwnerId, lease.Id, lease.TenantId, request.Amount, request.PaidOn, period,
            request.Reference, new StoredFile(key, upload.SafeFileName, upload.ContentType, upload.Content.LongLength,
                upload.Sha256), user.RequireUserId(), clock.Today());
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return await GetAsync(payment.Id, ct);
    }

    /// <summary>Marks the payment verified, applies it in the ledger and issues the receipt.</summary>
    public async Task<PaymentDto> VerifyAsync(Guid id, CancellationToken ct)
    {
        var leaseId = await OwnedLeaseIdAsync(id, ct);
        await db.InLockedTransactionAsync([$"lease:{leaseId}"], async c =>
        {
            var payment = await db.Payments.FirstAsync(p => p.Id == id, c);
            payment.Verify(user.RequireUserId(), user.DisplayName ?? "Landlord", clock.UtcNow);
            await receipts.IssueAsync(payment, c);
            await notifier.QueueAsync(payment.TenantId, Domain.Messaging.MessageTemplates.PaymentReceived,
                t => [t.FirstName, MessageFormat.Money(payment.Amount), payment.ReceiptNumber!], $"payment-received:{payment.Id}", c);
            await db.SaveChangesAsync(c);
            return true;
        }, ct);
        AppTelemetry.PaymentsVerified.Add(1);
        await dispatcher.TryDispatchAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<PaymentDto> RejectAsync(Guid id, RejectPaymentRequest request, CancellationToken ct)
    {
        await rejectValidator.ValidateAndThrowAsync(request, ct);
        await OwnedLeaseIdAsync(id, ct);
        var payment = await db.Payments.FirstAsync(p => p.Id == id, ct);
        payment.Reject(request.Reason, user.RequireUserId(), user.DisplayName ?? "Landlord", clock.UtcNow);
        await notifier.QueueAsync(payment.TenantId, Domain.Messaging.MessageTemplates.PaymentRejected,
            t => [t.FirstName, MessageFormat.Money(payment.Amount), request.Reason], $"payment-rejected:{payment.Id}", ct);
        await db.SaveChangesAsync(ct);
        await dispatcher.TryDispatchAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<(Stream Content, string ContentType, string FileName)> OpenFileAsync(Guid id, CancellationToken ct)
    {
        var p = await Visible().Where(x => x.Id == id)
                    .Select(x => new { x.FileKey, x.ContentType, x.FileName }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Payment", id);
        var stream = await files.OpenReadAsync(p.FileKey, ct) ?? throw new NotFoundException("File", id);
        return (stream, p.ContentType, p.FileName);
    }

    public async Task<(Stream Content, string FileName)> OpenReceiptAsync(Guid id, CancellationToken ct)
    {
        var p = await Visible().Where(x => x.Id == id)
                    .Select(x => new { x.ReceiptKey, x.ReceiptNumber }).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Payment", id);
        if (p.ReceiptKey is null) throw new NotFoundException("Receipt", id);
        var stream = await files.OpenReadAsync(p.ReceiptKey, ct) ?? throw new NotFoundException("Receipt", id);
        return (stream, $"{p.ReceiptNumber}.pdf");
    }

    private async Task<Guid> OwnedLeaseIdAsync(Guid paymentId, CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        var ownerId = user.RequireOwnerId();
        var leaseId = await db.Payments.Where(p => p.Id == paymentId && p.OwnerId == ownerId)
            .Select(p => (Guid?)p.LeaseId).FirstOrDefaultAsync(ct);
        return leaseId ?? throw new NotFoundException("Payment", paymentId);
    }

    private async Task<List<PaymentDto>> ToDtosAsync(IReadOnlyCollection<ProofOfPayment> payments, CancellationToken ct)
    {
        var leaseIds = payments.Select(p => p.LeaseId).Distinct().ToList();
        var leases = await db.Leases.IgnoreQueryFilters().AsNoTracking().Where(l => leaseIds.Contains(l.Id))
            .Select(l => new
            {
                l.Id, l.LeaseNumber, l.Version,
                TenantName = l.Tenant.FirstName + " " + l.Tenant.LastName,
                PropertyName = l.Unit.Property.Name, UnitLabel = l.Unit.Label,
            })
            .ToDictionaryAsync(l => l.Id, ct);

        return payments.Select(p =>
        {
            var l = leases[p.LeaseId];
            return new PaymentDto(p.Id, p.LeaseId, l.LeaseNumber, l.Version, p.TenantId, l.TenantName, l.PropertyName,
                l.UnitLabel, p.Amount, p.PaidOn, p.Period, p.Reference, p.FileName, p.ContentType, p.SizeBytes, p.Status,
                p.RejectionReason, p.ReviewedByName, p.ReviewedAt, p.ReceiptNumber, p.CreatedAt, ActionsFor(p));
        }).ToList();
    }

    private List<string> ActionsFor(ProofOfPayment p)
    {
        var actions = new List<string> { "viewFile" };
        if (!user.IsTenant() && p.Status == PaymentStatus.Submitted) actions.AddRange(["verify", "reject"]);
        if (p.ReceiptKey is not null) actions.Add("downloadReceipt");
        return actions;
    }
}

/// <summary>Builds and stores the receipt PDF for a payment that has just been verified.</summary>
public sealed class ReceiptFactory(
    IApplicationDbContext db, RentService rent, IReceiptRenderer renderer, IFileStorage files, IClock clock)
{
    /// <remarks>The payment must already be Verified (in memory); it is included in the ledger shown on the receipt.</remarks>
    public async Task IssueAsync(ProofOfPayment payment, CancellationToken ct)
    {
        var lease = await db.Leases.IgnoreQueryFilters().AsNoTracking()
            .Include(l => l.Unit).ThenInclude(u => u.Property).Include(l => l.Tenant)
            .FirstAsync(l => l.Id == payment.LeaseId, ct);

        var ledger = await rent.ComputeWithAsync(payment.LeaseId,
            new LedgerPaymentInput(payment.Id, payment.Amount, payment.Period, payment.ReviewedAt!.Value), ct);
        var allocated = ledger.Allocations.Where(a => a.PaymentId == payment.Id)
            .Select(a => new ReceiptLine(a.Period, a.Amount)).ToList();

        var number = await db.NextReceiptNumberAsync(clock.Today(), ct);
        var p = lease.Unit.Property;
        var model = new ReceiptPdfModel(
            number, clock.UtcNow, lease.LandlordName ?? "Landlord", lease.Tenant.FullName,
            p.Name, string.Join(", ", new[] { p.AddressLine1, p.AddressLine2, p.City, p.Province, p.PostalCode }
                .Where(x => !string.IsNullOrWhiteSpace(x))),
            lease.Unit.Label, lease.LeaseNumber, lease.Version, payment.Amount, payment.PaidOn, payment.Reference,
            allocated, payment.Amount - allocated.Sum(a => a.Amount), ledger.Balance, payment.ReviewedByName ?? "Landlord");

        var bytes = renderer.Render(model);
        var key = $"receipts/{payment.LeaseId:N}/{payment.Id:N}.pdf";
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            await files.SaveAsync(key, stream, "application/pdf", ct);
        }
        payment.AttachReceipt(number, key);
    }
}

public sealed record ReceiptLine(string Period, decimal Amount);

/// <param name="UnallocatedCredit">The part of this payment not used for rent yet (kept as credit).</param>
/// <param name="BalanceAfter">The lease balance after this payment. Negative means credit.</param>
public sealed record ReceiptPdfModel(
    string ReceiptNumber, DateTimeOffset IssuedAt, string LandlordName, string TenantName, string PropertyName,
    string PropertyAddress, string UnitLabel, string LeaseNumber, int LeaseVersion, decimal Amount, DateOnly PaidOn,
    string? Reference, IReadOnlyList<ReceiptLine> Lines, decimal UnallocatedCredit, decimal BalanceAfter,
    string VerifiedBy);

public interface IReceiptRenderer
{
    byte[] Render(ReceiptPdfModel model);
}

internal static class PagedResultExtensions
{
    public static PagedResult<TOut> MapItems<TIn, TOut>(this PagedResult<TIn> page, IReadOnlyList<TOut> items) =>
        new(items, page.Page, page.PageSize, page.TotalCount);
}
