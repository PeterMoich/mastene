using FluentValidation;
using TenantManager.Application.Common;
using TenantManager.Domain.Leases;

namespace TenantManager.Application.Leases;

/// <param name="EscalationPct">Rent increase at renewal. Defaults to the settings value when omitted.</param>
public sealed record LeaseRequest(
    Guid UnitId, Guid TenantId, DateOnly StartDate, decimal MonthlyRent, decimal DepositAmount, decimal? EscalationPct);

/// <param name="EscalationPct">Defaults to the escalation agreed in the current lease.</param>
/// <param name="StartDate">Only for a month-to-month lease; otherwise a renewal starts the day after the end date.</param>
public sealed record RenewLeaseRequest(decimal? EscalationPct, DateOnly? StartDate);

/// <param name="LastDay">The lease's last day. Ignored when cancelling a lease that is still waiting for signatures.</param>
public sealed record TerminateLeaseRequest(string Reason, DateOnly LastDay);

public sealed record LeaseListQuery : PagedQuery
{
    public LeaseStatus? Status { get; init; }
    public Guid? PropertyId { get; init; }
    public Guid? TenantId { get; init; }

    /// <summary>With <see cref="To"/>: leases whose term overlaps this date range.</summary>
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    /// <summary>Matches lease number or tenant name.</summary>
    public string? Search { get; init; }
}

public sealed record LeaseSummaryDto(
    Guid Id, string LeaseNumber, int Version, LeaseStatus Status, bool IsMonthToMonth,
    Guid PropertyId, string PropertyName, Guid UnitId, string UnitLabel, Guid TenantId, string TenantName,
    DateOnly StartDate, DateOnly EndDate, decimal MonthlyRent, DateOnly? TerminatesOn);

public sealed record LeaseSignatureDto(LeaseParty Party, string SignerName, DateTimeOffset SignedAt);

public sealed record LeaseDocumentDto(Guid Id, LeaseDocumentKind Kind, DateTimeOffset CreatedAt, long SizeBytes);

/// <param name="AllowedActions">
/// What the current user may do next: edit, discard, generate, sign, cancel, renew, terminate, download.
/// The API enforces the same rules; the list saves the UI from repeating them.
/// </param>
public sealed record LeaseDto(
    Guid Id, string LeaseNumber, int Version, LeaseStatus Status, bool IsMonthToMonth,
    Guid PropertyId, string PropertyName, string PropertyAddress, Guid UnitId, string UnitLabel,
    Guid TenantId, string TenantName, string? LandlordName,
    DateOnly StartDate, DateOnly EndDate, decimal MonthlyRent, decimal DepositAmount, decimal EscalationPct,
    Guid? PreviousLeaseId, Guid? RenewalLeaseId, DateOnly? ActivatedOn, DateOnly? TerminatesOn, string? TerminationReason,
    string? TemplateVersion,
    IReadOnlyList<LeaseSignatureDto> Signatures, IReadOnlyList<LeaseDocumentDto> Documents,
    IReadOnlyList<int> ExpiryNoticesRecorded, IReadOnlyList<string> AllowedActions,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>Everything the PDF needs, already formatted from the lease and its parties.</summary>
public sealed record LeasePdfModel(
    string LeaseNumber, int Version, bool IsRenewal, string TemplateVersion, string LandlordName,
    string TenantName, string TenantEmail, string TenantPhone,
    string PropertyName, string PropertyAddress, string UnitLabel,
    DateOnly StartDate, DateOnly EndDate, decimal MonthlyRent, decimal DepositAmount, decimal EscalationPct,
    IReadOnlyList<LeasePdfSignature> Signatures, DateTimeOffset GeneratedAt);

public sealed record LeasePdfSignature(string Party, string Name, DateTimeOffset SignedAt, string? IpAddress);

/// <summary>Renders the lease PDF (QuestPDF in Infrastructure, ADR-0004).</summary>
public interface ILeaseDocumentRenderer
{
    byte[] Render(LeasePdfModel model);
}

public sealed class LeaseRequestValidator : AbstractValidator<LeaseRequest>
{
    public LeaseRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.StartDate).InclusiveBetween(new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31))
            .WithMessage("Enter a valid start date.");
        RuleFor(x => x.MonthlyRent).GreaterThan(0).LessThanOrEqualTo(1_000_000)
            .PrecisionScale(18, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.DepositAmount).GreaterThanOrEqualTo(0).LessThanOrEqualTo(10_000_000)
            .PrecisionScale(18, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.EscalationPct).InclusiveBetween(0, 100).When(x => x.EscalationPct is not null);
    }
}

public sealed class RenewLeaseRequestValidator : AbstractValidator<RenewLeaseRequest>
{
    public RenewLeaseRequestValidator()
    {
        RuleFor(x => x.EscalationPct).InclusiveBetween(0, 100).When(x => x.EscalationPct is not null);
    }
}

public sealed class TerminateLeaseRequestValidator : AbstractValidator<TerminateLeaseRequest>
{
    public TerminateLeaseRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(Lease.ReasonMax);
        RuleFor(x => x.LastDay).InclusiveBetween(new DateOnly(2000, 1, 1), new DateOnly(2099, 12, 31))
            .WithMessage("Enter a valid last day.");
    }
}
