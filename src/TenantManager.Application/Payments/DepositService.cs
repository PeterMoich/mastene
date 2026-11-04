using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Application.Leases;
using TenantManager.Application.Settings;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;

namespace TenantManager.Application.Payments;

public sealed record ReceiveDepositRequest(decimal Amount, DateOnly ReceivedOn);

public sealed record DepositDeductionRequest(decimal Amount, string Reason);

public sealed record RefundDepositRequest(DateOnly RefundedOn);

public sealed record DepositDeductionDto(decimal Amount, string Reason, DateTimeOffset CreatedAt);

/// <param name="RefundDueBy">Lease end plus the refund days setting (LEGAL REVIEW NEEDED). Null while the lease runs.</param>
/// <param name="AllowedActions">receive, deduct, refund — for the current user.</param>
public sealed record DepositDto(
    string LeaseNumber, decimal AmountAgreed, decimal AmountReceived, DateOnly? ReceivedOn, DepositStatus Status,
    IReadOnlyList<DepositDeductionDto> Deductions, decimal TotalDeducted, decimal Remaining, decimal AmountRefunded,
    DateOnly? RefundedOn, bool LeaseHasEnded, DateOnly? LeaseEndedOn, DateOnly? RefundDueBy,
    IReadOnlyList<string> AllowedActions);

public sealed class ReceiveDepositRequestValidator : AbstractValidator<ReceiveDepositRequest>
{
    public ReceiveDepositRequestValidator() =>
        RuleFor(x => x.Amount).GreaterThan(0).LessThanOrEqualTo(10_000_000).PrecisionScale(18, 2, ignoreTrailingZeros: true);
}

public sealed class DepositDeductionRequestValidator : AbstractValidator<DepositDeductionRequest>
{
    public DepositDeductionRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(18, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(Deposit.ReasonMax);
    }
}

/// <summary>
/// BR13: the deposit for a lease, kept apart from the rent ledger. Shared by every version of a lease
/// (renewals keep the same deposit). Landlords record it; tenants can see it.
/// </summary>
public sealed class DepositService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    SettingsService settings,
    IValidator<ReceiveDepositRequest> receiveValidator,
    IValidator<DepositDeductionRequest> deductionValidator)
{
    public async Task<DepositDto> GetAsync(Guid leaseId, CancellationToken ct)
    {
        var chain = await ChainAsync(leaseId, ct);
        var deposit = await db.Deposits.AsNoTracking().Include(d => d.Deductions)
            .FirstOrDefaultAsync(d => d.LeaseNumber == chain.LeaseNumber, ct);
        return await ToDtoAsync(deposit, chain, ct);
    }

    public async Task<DepositDto> ReceiveAsync(Guid leaseId, ReceiveDepositRequest request, CancellationToken ct)
    {
        await receiveValidator.ValidateAndThrowAsync(request, ct);
        var chain = await ChainForWriteAsync(leaseId, ct);
        var deposit = await LoadOrCreateAsync(chain, ct);
        deposit.Receive(request.Amount, request.ReceivedOn, clock.Today());
        await db.SaveChangesAsync(ct);
        return await GetAsync(leaseId, ct);
    }

    public async Task<DepositDto> AddDeductionAsync(Guid leaseId, DepositDeductionRequest request, CancellationToken ct)
    {
        await deductionValidator.ValidateAndThrowAsync(request, ct);
        var chain = await ChainForWriteAsync(leaseId, ct);
        var deposit = await LoadOrCreateAsync(chain, ct);
        deposit.AddDeduction(request.Amount, request.Reason, chain.HasEnded);
        await db.SaveChangesAsync(ct);
        return await GetAsync(leaseId, ct);
    }

    public async Task<DepositDto> RefundAsync(Guid leaseId, RefundDepositRequest request, CancellationToken ct)
    {
        var chain = await ChainForWriteAsync(leaseId, ct);
        var deposit = await LoadOrCreateAsync(chain, ct);
        deposit.Refund(request.RefundedOn, chain.HasEnded, clock.Today());
        await db.SaveChangesAsync(ct);
        return await GetAsync(leaseId, ct);
    }

    private sealed record Chain(string LeaseNumber, Guid OwnerId, decimal AgreedDeposit, bool HasEnded, DateOnly? EndedOn);

    /// <summary>All versions of the lease: the deposit is agreed in version 1; the chain has ended when no version is in force.</summary>
    private async Task<Chain> ChainAsync(Guid leaseId, CancellationToken ct)
    {
        var number = await db.VisibleLeases(user).Where(l => l.Id == leaseId).Select(l => l.LeaseNumber)
                         .FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Lease", leaseId);
        var versions = await db.Leases.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.LeaseNumber == number)
            .Select(l => new { l.Version, l.OwnerId, l.Status, l.DepositAmount, l.ActivatedOn, l.EndDate, l.TerminatesOn })
            .ToListAsync(ct);

        var first = versions.OrderBy(v => v.Version).First();
        var inForce = LeaseTerm.InForce;
        var started = versions.Where(v => v.ActivatedOn is not null).ToList();
        var hasEnded = started.Count > 0 && !versions.Any(v => inForce.Contains(v.Status));
        DateOnly? endedOn = hasEnded
            ? started.Max(v => v.Status == LeaseStatus.Terminated && v.TerminatesOn is { } t ? t : v.EndDate)
            : null;
        return new Chain(number, first.OwnerId, first.DepositAmount, hasEnded, endedOn);
    }

    private async Task<Chain> ChainForWriteAsync(Guid leaseId, CancellationToken ct)
    {
        if (user.IsTenant()) throw new UnauthorizedAccessException();
        return await ChainAsync(leaseId, ct);
    }

    private async Task<Deposit> LoadOrCreateAsync(Chain chain, CancellationToken ct)
    {
        var deposit = await db.Deposits.Include(d => d.Deductions).FirstOrDefaultAsync(d => d.LeaseNumber == chain.LeaseNumber, ct);
        if (deposit is not null) return deposit;
        deposit = new Deposit(chain.OwnerId, chain.LeaseNumber, chain.AgreedDeposit);
        db.Deposits.Add(deposit);
        return deposit;
    }

    private async Task<DepositDto> ToDtoAsync(Deposit? d, Chain chain, CancellationToken ct)
    {
        var refundDays = (await settings.GetRentSettingsAsync(ct)).DepositRefundDays;
        var status = d?.Status ?? DepositStatus.AwaitingPayment;
        var actions = new List<string>();
        if (!user.IsTenant())
        {
            if (status == DepositStatus.AwaitingPayment) actions.Add("receive");
            if (status == DepositStatus.Held && chain.HasEnded) actions.AddRange(["deduct", "refund"]);
        }
        return new DepositDto(
            chain.LeaseNumber, d?.AmountAgreed ?? chain.AgreedDeposit, d?.AmountReceived ?? 0m, d?.ReceivedOn, status,
            d?.Deductions.OrderBy(x => x.CreatedAt).Select(x => new DepositDeductionDto(x.Amount, x.Reason, x.CreatedAt)).ToList()
                ?? [],
            d?.TotalDeducted ?? 0m, d?.Remaining ?? 0m, d?.AmountRefunded ?? 0m, d?.RefundedOn,
            chain.HasEnded, chain.EndedOn, chain.EndedOn?.AddDays(refundDays), actions);
    }
}
