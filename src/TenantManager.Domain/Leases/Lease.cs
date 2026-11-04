using TenantManager.Domain.Common;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Tenants;

namespace TenantManager.Domain.Leases;

/// <summary>What one lifecycle run changed on a lease.</summary>
[Flags]
public enum LeaseChange
{
    None = 0,
    MarkedExpiring = 1,
    NoticeRecorded = 2,
    MovedToMonthToMonth = 4,
    Ended = 8,
    Terminated = 16,
}

/// <summary>
/// A 12-month lease of one unit to one tenant. Aggregate root for its signatures, documents and
/// expiry notices. Only a Draft can be changed (BR04); once sent for signing it is kept exactly as
/// it is, and changes after that happen by renewal (a new version with the same lease number).
/// </summary>
public sealed class Lease : Entity, IOwned, IAuditable
{
    public const int ReasonMax = 500;
    public const int NameMax = 120;
    public const int TemplateVersionMax = 20;

    private readonly List<LeaseSignature> _signatures = [];
    private readonly List<LeaseDocument> _documents = [];
    private readonly List<LeaseExpiryNotice> _expiryNotices = [];

    private Lease() { } // EF Core

    public Guid OwnerId { get; private set; }

    /// <summary>Shared by every version in a renewal chain, e.g. L2026-0007.</summary>
    public string LeaseNumber { get; private set; } = string.Empty;

    /// <summary>1 for the first lease, +1 for each renewal.</summary>
    public int Version { get; private set; }

    public Guid? PreviousLeaseId { get; private set; }
    public Lease? Previous { get; private set; }
    public Guid UnitId { get; private set; }
    public Unit Unit { get; private set; } = null!;
    public Guid TenantId { get; private set; }
    public Tenant Tenant { get; private set; } = null!;
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public decimal MonthlyRent { get; private set; }

    /// <summary>The deposit agreed in this lease. Money actually held is tracked in Phase 3.</summary>
    public decimal DepositAmount { get; private set; }

    /// <summary>Rent increase applied when this lease is renewed.</summary>
    public decimal EscalationPct { get; private set; }

    public LeaseStatus Status { get; private set; }

    /// <summary>BR07: set when the end date passed with no renewal and settings say to continue month to month.</summary>
    public bool IsMonthToMonth { get; private set; }

    public DateOnly? ActivatedOn { get; private set; }

    /// <summary>Last day of the lease when termination is scheduled.</summary>
    public DateOnly? TerminatesOn { get; private set; }

    public string? TerminationReason { get; private set; }

    /// <summary>Snapshot of the landlord's name when the lease was generated.</summary>
    public string? LandlordName { get; private set; }

    /// <summary>The lease template wording used (LEGAL REVIEW NEEDED).</summary>
    public string? TemplateVersion { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<LeaseSignature> Signatures => _signatures.AsReadOnly();
    public IReadOnlyCollection<LeaseDocument> Documents => _documents.AsReadOnly();
    public IReadOnlyCollection<LeaseExpiryNotice> ExpiryNotices => _expiryNotices.AsReadOnly();

    public bool IsRenewal => PreviousLeaseId is not null || Previous is not null;
    public bool IsCurrent => Status is LeaseStatus.Active or LeaseStatus.Expiring;
    public bool HasSigned(LeaseParty party) => _signatures.Any(s => s.Party == party);
    public bool IsFullySigned => HasSigned(LeaseParty.Landlord) && HasSigned(LeaseParty.Tenant);
    public DateOnly EffectiveEndDate => LeaseTerm.EffectiveEnd(EndDate, IsMonthToMonth, TerminatesOn);

    public static Lease CreateDraft(string leaseNumber, Unit unit, Tenant tenant, DateOnly startDate,
        decimal monthlyRent, decimal depositAmount, decimal escalationPct, LeaseSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseNumber);
        var lease = new Lease
        {
            OwnerId = tenant.OwnerId,
            LeaseNumber = leaseNumber,
            Version = 1,
            Status = LeaseStatus.Draft,
        };
        lease.SetTerms(unit, tenant, startDate, monthlyRent, depositAmount, escalationPct, settings);
        return lease;
    }

    public void UpdateDraft(Unit unit, Tenant tenant, DateOnly startDate, decimal monthlyRent,
        decimal depositAmount, decimal escalationPct, LeaseSettings settings)
    {
        EnsureDraft();
        if (IsRenewal)
            throw new DomainRuleException("lease.renewal_not_editable",
                "A renewal's terms come from the current lease. Discard it and create the renewal again.",
                RuleKind.Conflict);
        SetTerms(unit, tenant, startDate, monthlyRent, depositAmount, escalationPct, settings);
    }

    public void EnsureDraft()
    {
        if (Status != LeaseStatus.Draft)
            throw new DomainRuleException("lease.not_draft",
                "Only a draft can be changed. Once a lease is sent for signing it is kept exactly as it is.",
                RuleKind.Conflict);
    }

    /// <summary>Draft → Pending signature, with the generated PDF attached.</summary>
    public void Generate(string landlordName, string templateVersion, LeaseDocument unsignedDocument)
    {
        EnsureDraft();
        if (unsignedDocument.Kind != LeaseDocumentKind.Unsigned)
            throw new ArgumentException("The first document must be the unsigned lease.", nameof(unsignedDocument));

        LandlordName = Guard.NotBlank(landlordName, "landlordName", NameMax);
        TemplateVersion = Guard.NotBlank(templateVersion, "templateVersion", TemplateVersionMax);
        _documents.Add(unsignedDocument);
        Status = LeaseStatus.PendingSignature;
    }

    /// <summary>
    /// Records one party's signature (BR06: both are needed). When the second signature lands and the
    /// start date has arrived, the lease becomes Active straight away; otherwise the daily job starts it.
    /// For a renewal, <see cref="Previous"/> must be loaded.
    /// </summary>
    /// <returns>True when this signature completed the lease.</returns>
    public bool Sign(LeaseParty party, Guid userId, string signerName, string? ipAddress, DateTimeOffset at,
        DateOnly today)
    {
        if (Status != LeaseStatus.PendingSignature)
            throw new DomainRuleException("lease.not_awaiting_signature", "This lease is not waiting for signatures.",
                RuleKind.Conflict);
        if (HasSigned(party))
            throw new DomainRuleException("lease.already_signed",
                $"The {party.ToString().ToLowerInvariant()} has already signed this lease.", RuleKind.Conflict);

        _signatures.Add(new LeaseSignature(party, userId, Guard.NotBlank(signerName, "signerName", NameMax),
            ipAddress, at));

        if (!IsFullySigned) return false;
        if (StartDate <= today) Activate(today);
        return true;
    }

    public void AddSignedDocument(LeaseDocument signedDocument)
    {
        if (!IsFullySigned) throw new InvalidOperationException("Both parties must sign before the signed copy is made.");
        if (signedDocument.Kind != LeaseDocumentKind.Signed)
            throw new ArgumentException("Expected the signed lease.", nameof(signedDocument));
        if (_documents.Any(d => d.Kind == LeaseDocumentKind.Signed))
            throw new InvalidOperationException("The signed copy already exists.");
        _documents.Add(signedDocument);
    }

    /// <summary>Pending signature → Active. A renewal moves the lease it replaces to Renewed.</summary>
    internal void Activate(DateOnly today)
    {
        if (Status != LeaseStatus.PendingSignature || !IsFullySigned || StartDate > today)
            throw new InvalidOperationException("This lease is not ready to start.");
        if (PreviousLeaseId is not null && Previous is null)
            throw new InvalidOperationException("Load the previous lease before starting a renewal.");

        if (Previous is { IsCurrent: true } previous) previous.Status = LeaseStatus.Renewed;
        Status = LeaseStatus.Active;
        ActivatedOn = today;
        RequireUnit().MarkOccupied();
    }

    /// <summary>
    /// BR06: a renewal is a new 12-month draft with the same lease number and version + 1. It starts the
    /// day after this lease ends (or on a chosen date if this lease is already month-to-month), with rent
    /// raised by the escalation percentage and rounded to the nearest rand.
    /// </summary>
    public Lease CreateRenewal(decimal escalationPct, DateOnly? startDate, DateOnly today, LeaseSettings settings)
    {
        if (!IsCurrent)
            throw new DomainRuleException("lease.not_renewable", "Only an active or expiring lease can be renewed.",
                RuleKind.Conflict);
        if (TerminatesOn is not null)
            throw new DomainRuleException("lease.termination_scheduled",
                "This lease is being terminated, so it can't be renewed.", RuleKind.Conflict);

        DateOnly start;
        if (IsMonthToMonth)
        {
            start = startDate ?? today;
            if (start < today)
                throw new DomainRuleException("startDate.in_past", "A renewal can't start in the past.");
        }
        else
        {
            start = EndDate.AddDays(1);
            if (startDate is { } chosen && chosen != start)
                throw new DomainRuleException("startDate.invalid",
                    $"A renewal starts the day after the current lease ends ({start:yyyy-MM-dd}).");
        }

        var pct = LeaseTerm.CheckEscalation(escalationPct, settings);
        return new Lease
        {
            OwnerId = OwnerId,
            LeaseNumber = LeaseNumber,
            Version = Version + 1,
            Previous = this,
            PreviousLeaseId = Id == Guid.Empty ? null : Id,
            UnitId = UnitId,
            Unit = Unit,
            TenantId = TenantId,
            Tenant = Tenant,
            StartDate = start,
            EndDate = LeaseTerm.EndDateFor(start),
            MonthlyRent = LeaseTerm.EscalatedRent(MonthlyRent, pct),
            DepositAmount = DepositAmount,
            EscalationPct = pct,
            Status = LeaseStatus.Draft,
        };
    }

    /// <summary>
    /// Ends a lease early. A lease still waiting for signatures is cancelled at once. An active lease
    /// gets a last day, which must respect the notice period from settings (LEGAL REVIEW NEEDED);
    /// the daily job moves it to Terminated after that day.
    /// </summary>
    public void Terminate(string reason, DateOnly lastDay, DateOnly today, LeaseSettings settings)
    {
        var cleanReason = Guard.NotBlank(reason, "reason", ReasonMax);
        switch (Status)
        {
            case LeaseStatus.Draft:
                throw new DomainRuleException("lease.is_draft", "Discard a draft instead of terminating it.",
                    RuleKind.Conflict);

            case LeaseStatus.PendingSignature:
                Status = LeaseStatus.Terminated;
                TerminatesOn = today;
                TerminationReason = cleanReason;
                return;

            case LeaseStatus.Active or LeaseStatus.Expiring:
                if (TerminatesOn is not null)
                    throw new DomainRuleException("lease.termination_scheduled",
                        $"Termination is already scheduled for {TerminatesOn:yyyy-MM-dd}.", RuleKind.Conflict);
                var earliest = today.AddDays(settings.TerminationNoticeDays);
                if (lastDay < earliest)
                    throw new DomainRuleException("lease.notice_too_short",
                        $"The last day must be on or after {earliest:yyyy-MM-dd} ({settings.TerminationNoticeDays} days' " +
                        "notice). The notice period is a setting marked LEGAL REVIEW NEEDED.");
                if (!IsMonthToMonth && lastDay > EndDate)
                    throw new DomainRuleException("lease.after_end",
                        $"This lease already ends on {EndDate:yyyy-MM-dd}, so it doesn't need terminating.");
                TerminatesOn = lastDay;
                TerminationReason = cleanReason;
                return;

            default:
                throw new DomainRuleException("lease.not_current", "This lease has already ended.", RuleKind.Conflict);
        }
    }

    /// <summary>
    /// One daily step for an Active or Expiring lease (BR05, BR07, scheduled termination).
    /// Starting leases and renewals is handled by <see cref="LeaseLifecycle"/>.
    /// </summary>
    internal LeaseChange Advance(DateOnly today, LeaseSettings settings, bool hasSignedRenewal)
    {
        if (!IsCurrent) return LeaseChange.None;

        if (TerminatesOn is { } lastDay && lastDay < today)
        {
            Status = LeaseStatus.Terminated;
            return LeaseChange.Terminated;
        }

        if (!IsMonthToMonth && EndDate < today)
        {
            if (settings.MonthToMonthOnLapse)
            {
                IsMonthToMonth = true;
                Status = LeaseStatus.Active;
                return LeaseChange.MovedToMonthToMonth;
            }
            Status = LeaseStatus.Ended;
            return LeaseChange.Ended;
        }

        if (IsMonthToMonth) return LeaseChange.None;

        var change = LeaseChange.None;
        var daysLeft = EndDate.DayNumber - today.DayNumber;
        if (Status == LeaseStatus.Active && daysLeft <= settings.FirstNoticeDays)
        {
            Status = LeaseStatus.Expiring;
            change |= LeaseChange.MarkedExpiring;
        }

        // One notice per threshold: record the tightest threshold crossed, once. A lease that starts
        // with 20 days left gets only the 30-day notice, not three at once.
        var crossed = settings.ExpiryNoticeDays.Where(d => daysLeft <= d).ToList();
        if (!hasSignedRenewal && crossed.Count > 0)
        {
            var tightest = crossed.Min();
            if (_expiryNotices.TrueForAll(n => n.DaysBefore != tightest))
            {
                _expiryNotices.Add(new LeaseExpiryNotice(tightest, today));
                change |= LeaseChange.NoticeRecorded;
            }
        }
        return change;
    }

    private void SetTerms(Unit unit, Tenant tenant, DateOnly startDate, decimal monthlyRent, decimal depositAmount,
        decimal escalationPct, LeaseSettings settings)
    {
        if (unit.Property.OwnerId != tenant.OwnerId)
            throw new DomainRuleException("lease.owner_mismatch", "The unit and tenant belong to different landlords.");
        if (unit.IsDeleted || unit.Property.IsDeleted || tenant.IsDeleted)
            throw new DomainRuleException("lease.party_removed", "The unit or tenant has been deleted.",
                RuleKind.Conflict);

        Unit = unit;
        UnitId = unit.Id;
        Tenant = tenant;
        TenantId = tenant.Id;
        StartDate = startDate;
        EndDate = LeaseTerm.EndDateFor(startDate);
        MonthlyRent = Guard.Money(monthlyRent, "monthlyRent", allowZero: false);
        DepositAmount = Guard.Money(depositAmount, "depositAmount", allowZero: true);
        EscalationPct = LeaseTerm.CheckEscalation(escalationPct, settings);
    }

    private Unit RequireUnit() =>
        Unit ?? throw new InvalidOperationException("Load the lease's unit before changing its status.");
}

public sealed class LeaseSignature : Entity, IAuditable
{
    private LeaseSignature() { } // EF Core

    internal LeaseSignature(LeaseParty party, Guid userId, string signerName, string? ipAddress, DateTimeOffset signedAt)
    {
        Party = party;
        UserId = userId;
        SignerName = signerName;
        IpAddress = ipAddress?.Length > 45 ? ipAddress[..45] : ipAddress;
        SignedAt = signedAt;
    }

    public Guid LeaseId { get; private set; }
    public LeaseParty Party { get; private set; }
    public Guid UserId { get; private set; }
    public string SignerName { get; private set; } = string.Empty;
    public string? IpAddress { get; private set; }
    public DateTimeOffset SignedAt { get; private set; }
}

/// <summary>A generated PDF. Never overwritten: the file is stored once and identified by its hash.</summary>
public sealed class LeaseDocument : Entity, IAuditable
{
    private LeaseDocument() { } // EF Core

    public LeaseDocument(LeaseDocumentKind kind, string blobKey, string sha256, long sizeBytes)
    {
        Kind = kind;
        BlobKey = blobKey;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    public Guid LeaseId { get; private set; }
    public LeaseDocumentKind Kind { get; private set; }
    public string BlobKey { get; private set; } = string.Empty;
    public string Sha256 { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
}

/// <summary>
/// BR05: an expiry notice that is due (90, 60 or 30 days before the end, per settings).
/// Recorded here in Phase 2; Phase 5 sends them to the tenant over WhatsApp.
/// </summary>
public sealed class LeaseExpiryNotice : Entity, IAuditable
{
    private LeaseExpiryNotice() { } // EF Core

    internal LeaseExpiryNotice(int daysBefore, DateOnly recordedOn)
    {
        DaysBefore = daysBefore;
        RecordedOn = recordedOn;
    }

    public Guid LeaseId { get; private set; }
    public int DaysBefore { get; private set; }
    public DateOnly RecordedOn { get; private set; }
}
