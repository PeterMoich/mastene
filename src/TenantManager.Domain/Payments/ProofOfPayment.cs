using TenantManager.Domain.Common;

namespace TenantManager.Domain.Payments;

public enum PaymentStatus
{
    Submitted,
    Verified,
    Rejected,
}

/// <summary>
/// A tenant's proof that they paid rent (a bank confirmation PDF or a photo of one), against one lease
/// and optionally one month. Submitted → Verified or Rejected (BR11). Only verified payments count in
/// the ledger. The file is identified by its SHA-256 so the same file can't be submitted twice (BR10).
/// </summary>
public sealed class ProofOfPayment : Entity, IOwned, IAuditable
{
    public const int ReferenceMax = 50;
    public const int ReasonMax = 500;
    public const int FileNameMax = 120;

    private ProofOfPayment() { } // EF Core

    public ProofOfPayment(Guid ownerId, Guid leaseId, Guid tenantId, decimal amount, DateOnly paidOn,
        BillingPeriod? period, string? reference, StoredFile file, Guid submittedBy, DateOnly today)
    {
        if (paidOn > today)
            throw new DomainRuleException("paidOn.in_future", "The payment date can't be in the future.");
        OwnerId = ownerId;
        LeaseId = leaseId;
        TenantId = tenantId;
        Amount = Guard.Money(amount, "amount", allowZero: false);
        PaidOn = paidOn;
        Period = period?.ToString();
        Reference = Guard.Optional(reference, "reference", ReferenceMax);
        FileKey = file.Key;
        FileName = file.FileName.Length > FileNameMax ? file.FileName[..FileNameMax] : file.FileName;
        ContentType = file.ContentType;
        SizeBytes = file.SizeBytes;
        Sha256 = file.Sha256;
        SubmittedBy = submittedBy;
        Status = PaymentStatus.Submitted;
    }

    public Guid OwnerId { get; private set; }
    public Guid LeaseId { get; private set; }
    public Guid TenantId { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly PaidOn { get; private set; }

    /// <summary>"yyyy-MM" when the tenant picked a month; otherwise oldest unpaid months are paid first.</summary>
    public string? Period { get; private set; }

    public string? Reference { get; private set; }
    public string FileKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public Guid SubmittedBy { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? RejectionReason { get; private set; }
    public Guid? ReviewedBy { get; private set; }
    public string? ReviewedByName { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public string? ReceiptNumber { get; private set; }
    public string? ReceiptKey { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public void Verify(Guid reviewerId, string reviewerName, DateTimeOffset at)
    {
        EnsureSubmitted();
        Status = PaymentStatus.Verified;
        ReviewedBy = reviewerId;
        ReviewedByName = Guard.NotBlank(reviewerName, "reviewerName", 120);
        ReviewedAt = at;
    }

    /// <summary>BR11: a rejection always says why, so the tenant knows what to fix.</summary>
    public void Reject(string reason, Guid reviewerId, string reviewerName, DateTimeOffset at)
    {
        var cleanReason = Guard.NotBlank(reason, "reason", ReasonMax);
        EnsureSubmitted();
        Status = PaymentStatus.Rejected;
        RejectionReason = cleanReason;
        ReviewedBy = reviewerId;
        ReviewedByName = Guard.NotBlank(reviewerName, "reviewerName", 120);
        ReviewedAt = at;
    }

    public void AttachReceipt(string receiptNumber, string receiptKey)
    {
        if (Status != PaymentStatus.Verified) throw new InvalidOperationException("Only a verified payment gets a receipt.");
        if (ReceiptNumber is not null) throw new InvalidOperationException("The receipt already exists.");
        ReceiptNumber = receiptNumber;
        ReceiptKey = receiptKey;
    }

    private void EnsureSubmitted()
    {
        if (Status != PaymentStatus.Submitted)
            throw new DomainRuleException("payment.already_reviewed",
                $"This payment has already been {Status.ToString().ToLowerInvariant()}.", RuleKind.Conflict);
    }
}

/// <summary>A file that passed validation and was stored.</summary>
public sealed record StoredFile(string Key, string FileName, string ContentType, long SizeBytes, string Sha256);
