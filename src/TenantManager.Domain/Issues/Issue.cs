using TenantManager.Domain.Common;
using TenantManager.Domain.Payments;

namespace TenantManager.Domain.Issues;

public enum IssueCategory
{
    Plumbing,
    Electrical,
    Appliance,
    Structural,
    Pest,
    Security,
    Garden,
    Other,
}

public enum IssuePriority
{
    Low,
    Medium,
    High,
    Urgent,
}

public enum IssueStatus
{
    Open,
    Acknowledged,
    InProgress,
    Resolved,
    Closed,
}

public enum IssueEventKind
{
    Opened,
    Acknowledged,
    Started,
    ContractorAssigned,
    ContractorRemoved,
    PriorityChanged,
    Resolved,
    Reopened,
    Closed,
    AutoClosed,
    ResponseTargetMissed,
    ResolutionTargetMissed,
}

/// <summary>Who did something to an issue. The name is a snapshot so the history reads the same later.</summary>
public sealed record IssueActor(Guid UserId, string Name, bool IsTenant);

/// <summary>
/// A maintenance issue on a unit (BR14). Status flow: Open → Acknowledged → In progress → Resolved →
/// Closed, with the tenant able to reopen a resolved issue. Each step is timestamped, and target
/// response and resolution times come from the issue's priority (BR15). Escalation is a flag set when a
/// target is missed, not a status, so the issue keeps moving through the normal flow.
/// </summary>
public sealed class Issue : Entity, IOwned, IAuditable
{
    public const int TitleMax = 120;
    public const int DescriptionMax = 4000;
    public const int NoteMax = 1000;
    public const int MaxPhotos = 5;

    private readonly List<IssueComment> _comments = [];
    private readonly List<IssuePhoto> _photos = [];
    private readonly List<IssueEvent> _events = [];

    private Issue() { } // EF Core

    /// <param name="leaseId">The tenant's lease, when the issue is about a let unit; null for a vacant unit.</param>
    public Issue(Guid ownerId, Guid unitId, Guid? leaseId, Guid? tenantId, IssueCategory category,
        IssuePriority priority, string title, string description, IssueActor by, SlaTargets targets,
        DateTimeOffset now)
    {
        if ((leaseId is null) != (tenantId is null))
            throw new ArgumentException("A lease and its tenant are given together.");
        OwnerId = ownerId;
        UnitId = unitId;
        LeaseId = leaseId;
        TenantId = tenantId;
        Category = category;
        Priority = priority;
        Title = Guard.NotBlank(title, "title", TitleMax);
        Description = Guard.NotBlank(description, "description", DescriptionMax);
        ReportedBy = by.UserId;
        ReportedByName = by.Name;
        Status = IssueStatus.Open;
        OpenedAt = now;
        SetDueDates(targets);
        Record(IssueEventKind.Opened, by, now, $"{priority} priority");
    }

    public Guid OwnerId { get; private set; }
    public Guid UnitId { get; private set; }
    public Guid? LeaseId { get; private set; }

    /// <summary>The tenant who can see the issue, confirm it is fixed, or reopen it. Null for a vacant unit.</summary>
    public Guid? TenantId { get; private set; }

    public IssueCategory Category { get; private set; }
    public IssuePriority Priority { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public IssueStatus Status { get; private set; }
    public Guid? ContractorId { get; private set; }
    public Guid ReportedBy { get; private set; }
    public string ReportedByName { get; private set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Acknowledge by this time (from the priority's response target).</summary>
    public DateTimeOffset ResponseDueAt { get; private set; }

    /// <summary>Resolve by this time (from the priority's resolution target).</summary>
    public DateTimeOffset ResolutionDueAt { get; private set; }

    public DateTimeOffset? ResponseMissedAt { get; private set; }
    public DateTimeOffset? ResolutionMissedAt { get; private set; }

    /// <summary>BR15: set when either target was missed. Kept afterwards, as a record that it happened.</summary>
    public bool IsEscalated { get; private set; }

    public string? ResolutionNote { get; private set; }
    public int ReopenCount { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<IssueComment> Comments => _comments.AsReadOnly();
    public IReadOnlyCollection<IssuePhoto> Photos => _photos.AsReadOnly();
    public IReadOnlyCollection<IssueEvent> Events => _events.AsReadOnly();

    public bool IsFinished => Status == IssueStatus.Closed;

    /// <summary>True when acknowledged before the response target, false when late, null while still waiting.</summary>
    public bool? ResponseMet(DateTimeOffset now) =>
        AcknowledgedAt is { } at ? at <= ResponseDueAt : now > ResponseDueAt ? false : null;

    public bool? ResolutionMet(DateTimeOffset now) =>
        ResolvedAt is { } at ? at <= ResolutionDueAt : now > ResolutionDueAt ? false : null;

    public void Acknowledge(IssueActor by, DateTimeOffset now)
    {
        RequireLandlord(by);
        RequireStatus(IssueStatus.Open, "acknowledged");
        AcknowledgedAt = now;
        Status = IssueStatus.Acknowledged;
        Record(IssueEventKind.Acknowledged, by, now);
    }

    /// <summary>Work has started. Starting an issue that hasn't been acknowledged also acknowledges it.</summary>
    public void Start(IssueActor by, DateTimeOffset now)
    {
        RequireLandlord(by);
        if (Status == IssueStatus.Open) Acknowledge(by, now);
        RequireStatus(IssueStatus.Acknowledged, "started");
        StartedAt = now;
        Status = IssueStatus.InProgress;
        Record(IssueEventKind.Started, by, now);
    }

    public void Resolve(string? note, IssueActor by, DateTimeOffset now)
    {
        RequireLandlord(by);
        RequireStatus(IssueStatus.InProgress, "resolved");
        ResolutionNote = Guard.Optional(note, "note", NoteMax);
        ResolvedAt = now;
        Status = IssueStatus.Resolved;
        Record(IssueEventKind.Resolved, by, now, ResolutionNote);
    }

    /// <summary>
    /// Confirms the fix. The tenant does this for their own issue; the landlord only for an issue with
    /// no tenant (a vacant unit). Otherwise the system closes it after the configured number of days.
    /// </summary>
    public void Close(IssueActor by, DateTimeOffset now)
    {
        RequireStatus(IssueStatus.Resolved, "closed");
        if (TenantId is not null && !by.IsTenant)
            throw new DomainRuleException("issue.tenant_confirms",
                "The tenant confirms the fix. If they don't, the issue closes by itself after the set number of days.",
                RuleKind.Conflict);
        if (TenantId is null && by.IsTenant) throw new UnauthorizedAccessException();
        ClosedAt = now;
        Status = IssueStatus.Closed;
        Record(IssueEventKind.Closed, by, now);
    }

    /// <summary>Not fixed after all: back to In progress. The resolution target stays as it was.</summary>
    public void Reopen(string reason, IssueActor by, DateTimeOffset now)
    {
        var cleanReason = Guard.NotBlank(reason, "reason", NoteMax);
        RequireStatus(IssueStatus.Resolved, "reopened");
        if (TenantId is not null && !by.IsTenant)
            throw new DomainRuleException("issue.tenant_reopens", "Only the tenant can reopen their issue.",
                RuleKind.Conflict);
        if (TenantId is null && by.IsTenant) throw new UnauthorizedAccessException();
        ResolvedAt = null;
        ResolutionNote = null;
        ReopenCount++;
        Status = IssueStatus.InProgress;
        Record(IssueEventKind.Reopened, by, now, cleanReason);
    }

    /// <param name="contractorName">For the history; null when removing the contractor.</param>
    public void AssignContractor(Guid? contractorId, string? contractorName, IssueActor by, DateTimeOffset now)
    {
        RequireLandlord(by);
        RequireNotFinished();
        if (ContractorId == contractorId) return;
        ContractorId = contractorId;
        if (contractorId is null) Record(IssueEventKind.ContractorRemoved, by, now);
        else Record(IssueEventKind.ContractorAssigned, by, now, contractorName);
    }

    /// <summary>
    /// The landlord can correct the priority until the issue is resolved. Targets are recalculated
    /// from when the issue was opened. A target already missed stays recorded as missed.
    /// </summary>
    public void ChangePriority(IssuePriority priority, IssueActor by, SlaTargets targets, DateTimeOffset now)
    {
        RequireLandlord(by);
        if (Status is IssueStatus.Resolved or IssueStatus.Closed)
            throw new DomainRuleException("issue.priority_locked",
                "The priority can't be changed once the issue is resolved.", RuleKind.Conflict);
        if (Priority == priority) return;
        var from = Priority;
        Priority = priority;
        SetDueDates(targets);
        Record(IssueEventKind.PriorityChanged, by, now, $"{from} to {priority}");
    }

    public IssueComment AddComment(string body, IssueActor by, DateTimeOffset now)
    {
        RequireNotFinished();
        var comment = new IssueComment(Guard.NotBlank(body, "body", IssueComment.BodyMax), by, now);
        _comments.Add(comment);
        return comment;
    }

    /// <summary>BR12: at most five photos per issue, each file once.</summary>
    public IssuePhoto AddPhoto(StoredFile file, IssueActor by, DateTimeOffset now)
    {
        RequireNotFinished();
        if (_photos.Count >= MaxPhotos)
            throw new DomainRuleException("issue.too_many_photos", $"An issue can have at most {MaxPhotos} photos.",
                RuleKind.Conflict);
        if (_photos.Any(p => p.Sha256 == file.Sha256))
            throw new DomainRuleException("issue.duplicate_photo", "This photo has already been added.", RuleKind.Conflict);
        var photo = new IssuePhoto(file, by, now);
        _photos.Add(photo);
        return photo;
    }

    /// <summary>BR15: flags a missed response or resolution target. Returns true when something new was missed.</summary>
    public bool CheckTargets(DateTimeOffset now)
    {
        if (Status == IssueStatus.Closed) return false;
        var changed = false;
        if (ResponseMissedAt is null && AcknowledgedAt is null && now > ResponseDueAt)
        {
            ResponseMissedAt = now;
            Record(IssueEventKind.ResponseTargetMissed, null, now);
            changed = true;
        }
        if (ResolutionMissedAt is null && ResolvedAt is null && now > ResolutionDueAt)
        {
            ResolutionMissedAt = now;
            Record(IssueEventKind.ResolutionTargetMissed, null, now);
            changed = true;
        }
        if (changed) IsEscalated = true;
        return changed;
    }

    /// <summary>Closes a resolved issue the tenant hasn't confirmed or reopened within the set number of days.</summary>
    public bool AutoClose(DateTimeOffset now, int afterDays)
    {
        if (Status != IssueStatus.Resolved || ResolvedAt is not { } resolved || now < resolved.AddDays(afterDays))
            return false;
        ClosedAt = now;
        Status = IssueStatus.Closed;
        Record(IssueEventKind.AutoClosed, null, now, $"No reply within {afterDays} days of the fix");
        return true;
    }

    private void SetDueDates(SlaTargets targets)
    {
        var target = targets.For(Priority);
        ResponseDueAt = OpenedAt.AddHours(target.ResponseHours);
        ResolutionDueAt = OpenedAt.AddHours(target.ResolutionHours);
    }

    private void Record(IssueEventKind kind, IssueActor? by, DateTimeOffset at, string? note = null) =>
        _events.Add(new IssueEvent(kind, by, at, note));

    private static void RequireLandlord(IssueActor by)
    {
        if (by.IsTenant) throw new UnauthorizedAccessException("Only the landlord can do this.");
    }

    private void RequireStatus(IssueStatus expected, string verb)
    {
        if (Status != expected)
            throw new DomainRuleException("issue.invalid_transition",
                $"An issue that is {Describe(Status)} can't be {verb}.", RuleKind.Conflict);
    }

    private void RequireNotFinished()
    {
        if (Status == IssueStatus.Closed)
            throw new DomainRuleException("issue.closed", "This issue is closed.", RuleKind.Conflict);
    }

    private static string Describe(IssueStatus status) => status switch
    {
        IssueStatus.InProgress => "in progress",
        _ => status.ToString().ToLowerInvariant(),
    };
}

public sealed class IssueComment : Entity, IAuditable
{
    public const int BodyMax = 2000;

    private IssueComment() { } // EF Core

    internal IssueComment(string body, IssueActor by, DateTimeOffset at)
    {
        Body = body;
        AuthorId = by.UserId;
        AuthorName = by.Name;
        ByTenant = by.IsTenant;
        At = at;
    }

    public Guid IssueId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string AuthorName { get; private set; } = string.Empty;
    public bool ByTenant { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public DateTimeOffset At { get; private set; }
}

public sealed class IssuePhoto : Entity, IAuditable
{
    private IssuePhoto() { } // EF Core

    internal IssuePhoto(StoredFile file, IssueActor by, DateTimeOffset at)
    {
        FileKey = file.Key;
        FileName = file.FileName.Length > 120 ? file.FileName[..120] : file.FileName;
        ContentType = file.ContentType;
        SizeBytes = file.SizeBytes;
        Sha256 = file.Sha256;
        UploadedBy = by.UserId;
        UploadedByName = by.Name;
        At = at;
    }

    public Guid IssueId { get; private set; }
    public string FileKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public Guid UploadedBy { get; private set; }
    public string UploadedByName { get; private set; } = string.Empty;
    public DateTimeOffset At { get; private set; }
}

/// <summary>One step in an issue's history. Written once, never changed; the timeline is built from these.</summary>
public sealed class IssueEvent : Entity
{
    private IssueEvent() { } // EF Core

    internal IssueEvent(IssueEventKind kind, IssueActor? by, DateTimeOffset at, string? note)
    {
        Kind = kind;
        ById = by?.UserId;
        ByName = by?.Name ?? "System";
        At = at;
        Note = note;
    }

    public Guid IssueId { get; private set; }
    public IssueEventKind Kind { get; private set; }
    public Guid? ById { get; private set; }
    public string ByName { get; private set; } = string.Empty;
    public DateTimeOffset At { get; private set; }
    public string? Note { get; private set; }
}
