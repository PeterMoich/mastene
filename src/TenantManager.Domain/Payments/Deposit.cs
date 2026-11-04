using TenantManager.Domain.Common;

namespace TenantManager.Domain.Payments;

public enum DepositStatus
{
    /// <summary>Agreed in the lease, not received yet.</summary>
    AwaitingPayment,

    Held,

    /// <summary>Returned after deductions (or fully kept against deductions).</summary>
    PartiallyRefunded,

    Refunded,
}

/// <summary>
/// BR13: the deposit, recorded separately from rent. One per lease number, so it carries over when
/// the lease is renewed. Deductions (with reasons) and the refund are recorded only after the lease
/// has ended, and deductions can never exceed what is held. How the deposit must be held and when it
/// must be refunded are legal questions (settings marked LEGAL REVIEW NEEDED).
/// </summary>
public sealed class Deposit : Entity, IOwned, IAuditable
{
    public const int ReasonMax = 300;

    private readonly List<DepositDeduction> _deductions = [];

    private Deposit() { } // EF Core

    public Deposit(Guid ownerId, string leaseNumber, decimal amountAgreed)
    {
        OwnerId = ownerId;
        LeaseNumber = leaseNumber;
        AmountAgreed = Guard.Money(amountAgreed, "amountAgreed", allowZero: true);
        Status = DepositStatus.AwaitingPayment;
    }

    public Guid OwnerId { get; private set; }
    public string LeaseNumber { get; private set; } = string.Empty;
    public decimal AmountAgreed { get; private set; }
    public decimal AmountReceived { get; private set; }
    public DateOnly? ReceivedOn { get; private set; }
    public DepositStatus Status { get; private set; }
    public decimal AmountRefunded { get; private set; }
    public DateOnly? RefundedOn { get; private set; }
    public IReadOnlyCollection<DepositDeduction> Deductions => _deductions.AsReadOnly();
    public decimal TotalDeducted => _deductions.Sum(d => d.Amount);
    public decimal Remaining => AmountReceived - TotalDeducted;

    public void Receive(decimal amount, DateOnly receivedOn, DateOnly today)
    {
        if (Status != DepositStatus.AwaitingPayment)
            throw new DomainRuleException("deposit.already_received", "The deposit has already been recorded.",
                RuleKind.Conflict);
        if (receivedOn > today)
            throw new DomainRuleException("receivedOn.in_future", "The date received can't be in the future.");
        AmountReceived = Guard.Money(amount, "amount", allowZero: false);
        ReceivedOn = receivedOn;
        Status = DepositStatus.Held;
    }

    public void AddDeduction(decimal amount, string reason, bool leaseHasEnded)
    {
        EnsureHeldAndEnded(leaseHasEnded);
        var value = Guard.Money(amount, "amount", allowZero: false);
        if (value > Remaining)
            throw new DomainRuleException("deposit.deduction_too_large",
                $"Deductions can't be more than the deposit held (R{Remaining:N2} left).");
        _deductions.Add(new DepositDeduction(value, Guard.NotBlank(reason, "reason", ReasonMax)));
    }

    public void Refund(DateOnly refundedOn, bool leaseHasEnded, DateOnly today)
    {
        EnsureHeldAndEnded(leaseHasEnded);
        if (refundedOn > today)
            throw new DomainRuleException("refundedOn.in_future", "The refund date can't be in the future.");
        AmountRefunded = Remaining;
        RefundedOn = refundedOn;
        Status = TotalDeducted > 0 ? DepositStatus.PartiallyRefunded : DepositStatus.Refunded;
    }

    private void EnsureHeldAndEnded(bool leaseHasEnded)
    {
        if (Status != DepositStatus.Held)
            throw new DomainRuleException("deposit.not_held",
                Status == DepositStatus.AwaitingPayment ? "The deposit hasn't been received yet." : "The deposit has already been refunded.",
                RuleKind.Conflict);
        if (!leaseHasEnded)
            throw new DomainRuleException("deposit.lease_not_ended",
                "Deductions and refunds are recorded after the lease has ended.", RuleKind.Conflict);
    }
}

public sealed class DepositDeduction : Entity, IAuditable
{
    private DepositDeduction() { } // EF Core

    internal DepositDeduction(decimal amount, string reason)
    {
        Amount = amount;
        Reason = reason;
    }

    public Guid DepositId { get; private set; }
    public decimal Amount { get; private set; }
    public string Reason { get; private set; } = string.Empty;
}
