using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;

namespace TenantManager.Domain.Inspections;

public enum InspectionKind
{
    MoveIn,
    MoveOut,
}

public enum InspectionStatus
{
    Draft,
    Completed,
}

public enum ItemCondition
{
    Good,
    Fair,
    Poor,
    Damaged,
    Missing,
}

/// <summary>
/// The move-in or move-out checklist for a lease, with photos per area. The landlord fills it in as a
/// draft, then completes it, after which it can't change. The tenant then acknowledges it, optionally
/// with a comment if they disagree. There is one of each kind per lease number, so a renewal doesn't
/// need a new move-in inspection. Whether the inspection must be done jointly, and what it means for
/// deposit deductions, are legal questions (LEGAL REVIEW NEEDED, ADR-0011).
/// </summary>
public sealed class Inspection : Entity, IOwned, IAuditable
{
    public const int AreaMax = 80;
    public const int NotesMax = 1000;
    public const int CommentMax = 1000;
    public const int MaxItems = 40;
    public const int MaxPhotosPerItem = 5;

    /// <summary>A starting checklist for a typical flat or cottage; the landlord adds or removes areas.</summary>
    public static readonly IReadOnlyList<string> DefaultAreas =
    [
        "Entrance and front door",
        "Lounge",
        "Kitchen",
        "Kitchen appliances",
        "Bathroom",
        "Bedroom 1",
        "Walls and ceilings",
        "Floors",
        "Windows and burglar bars",
        "Electrical (plugs, lights, DB board)",
        "Plumbing (taps, geyser, toilet)",
        "Keys, remotes and access tags",
    ];

    private readonly List<InspectionItem> _items = [];

    private Inspection() { } // EF Core

    private Inspection(Lease lease, InspectionKind kind, IEnumerable<string> areas, DateOnly today)
    {
        OwnerId = lease.OwnerId;
        LeaseId = lease.Id;
        LeaseNumber = lease.LeaseNumber;
        UnitId = lease.UnitId;
        TenantId = lease.TenantId;
        Kind = kind;
        Status = InspectionStatus.Draft;
        InspectedOn = today;
        var order = 0;
        foreach (var area in areas.Distinct(StringComparer.OrdinalIgnoreCase))
            _items.Add(new InspectionItem(Guard.NotBlank(area, "area", AreaMax), order++));
    }

    /// <summary>Move-in: once the lease has been sent for signing, until it ends.</summary>
    public static Inspection StartMoveIn(Lease lease, DateOnly today)
    {
        if (!LeaseTerm.InForce.Contains(lease.Status))
            throw new DomainRuleException("inspection.lease_not_in_force",
                "A move-in inspection is done for a lease that has been sent for signing or is running.",
                RuleKind.Conflict);
        return new Inspection(lease, InspectionKind.MoveIn, DefaultAreas, today);
    }

    /// <summary>Move-out: once the lease has started. Uses the move-in areas when there was a move-in inspection.</summary>
    public static Inspection StartMoveOut(Lease lease, Inspection? moveIn, DateOnly today)
    {
        if (lease.ActivatedOn is null ||
            lease.Status is not (LeaseStatus.Active or LeaseStatus.Expiring or LeaseStatus.Ended or LeaseStatus.Terminated))
            throw new DomainRuleException("inspection.lease_not_started",
                "A move-out inspection is done for a lease that has started.", RuleKind.Conflict);
        var areas = moveIn?.Items.OrderBy(i => i.Order).Select(i => i.Area).ToList() ?? [.. DefaultAreas];
        return new Inspection(lease, InspectionKind.MoveOut, areas, today);
    }

    public Guid OwnerId { get; private set; }

    /// <summary>The lease version it was started on.</summary>
    public Guid LeaseId { get; private set; }

    public string LeaseNumber { get; private set; } = string.Empty;
    public Guid UnitId { get; private set; }
    public Guid TenantId { get; private set; }
    public InspectionKind Kind { get; private set; }
    public InspectionStatus Status { get; private set; }
    public DateOnly InspectedOn { get; private set; }
    public string? GeneralNotes { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? CompletedByName { get; private set; }
    public DateTimeOffset? TenantAcknowledgedAt { get; private set; }

    /// <summary>The tenant's comment when acknowledging, e.g. where they disagree.</summary>
    public string? TenantComment { get; private set; }

    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyCollection<InspectionItem> Items => _items.AsReadOnly();

    public void UpdateDetails(DateOnly inspectedOn, string? generalNotes, DateOnly today)
    {
        RequireDraft();
        if (inspectedOn > today)
            throw new DomainRuleException("inspectedOn.in_future", "The inspection date can't be in the future.");
        InspectedOn = inspectedOn;
        GeneralNotes = Guard.Optional(generalNotes, "generalNotes", NotesMax);
    }

    public InspectionItem AddItem(string area)
    {
        RequireDraft();
        var clean = Guard.NotBlank(area, "area", AreaMax);
        if (_items.Count >= MaxItems)
            throw new DomainRuleException("inspection.too_many_items", $"A checklist can have at most {MaxItems} areas.");
        if (_items.Any(i => string.Equals(i.Area, clean, StringComparison.OrdinalIgnoreCase)))
            throw new DomainRuleException("area.duplicate", "This area is already on the checklist.", RuleKind.Conflict);
        var item = new InspectionItem(clean, _items.Count == 0 ? 0 : _items.Max(i => i.Order) + 1);
        _items.Add(item);
        return item;
    }

    public void RemoveItem(Guid itemId)
    {
        RequireDraft();
        _items.Remove(Item(itemId));
    }

    public void UpdateItem(Guid itemId, ItemCondition? condition, string? notes) =>
        UpdateItem(Item(itemId), condition, notes);

    // Overloads taking the item itself: ids are assigned by the database, so new items can't be found by id yet.
    internal void UpdateItem(InspectionItem item, ItemCondition? condition, string? notes)
    {
        RequireDraft();
        item.Update(condition, Guard.Optional(notes, "notes", NotesMax));
    }

    public InspectionPhoto AddPhoto(Guid itemId, StoredFile file, DateTimeOffset now) => AddPhoto(Item(itemId), file, now);

    internal InspectionPhoto AddPhoto(InspectionItem item, StoredFile file, DateTimeOffset now)
    {
        RequireDraft();
        if (item.Photos.Count >= MaxPhotosPerItem)
            throw new DomainRuleException("inspection.too_many_photos",
                $"Each area can have at most {MaxPhotosPerItem} photos.", RuleKind.Conflict);
        return item.AddPhoto(file, now);
    }

    /// <summary>Every area needs a condition before the inspection can be completed. It can't change afterwards.</summary>
    public void Complete(string byName, DateTimeOffset now)
    {
        RequireDraft();
        if (_items.Count == 0)
            throw new DomainRuleException("inspection.empty", "Add at least one area to the checklist.");
        var missing = _items.Where(i => i.Condition is null).Select(i => i.Area).ToList();
        if (missing.Count > 0)
            throw new DomainRuleException("inspection.items_unchecked",
                $"Give a condition for every area first: {string.Join(", ", missing)}.");
        Status = InspectionStatus.Completed;
        CompletedAt = now;
        CompletedByName = Guard.NotBlank(byName, "byName", 120);
    }

    /// <summary>POPIA erasure: the tenant's comment goes; the checklist (the condition of the unit) stays.</summary>
    public void RedactTenantComment()
    {
        if (TenantComment is not null) TenantComment = Privacy.Removed;
    }

    public void Acknowledge(string? comment, DateTimeOffset now)
    {
        if (Status != InspectionStatus.Completed)
            throw new DomainRuleException("inspection.not_completed", "The inspection hasn't been completed yet.",
                RuleKind.Conflict);
        if (TenantAcknowledgedAt is not null)
            throw new DomainRuleException("inspection.already_acknowledged", "You have already acknowledged this inspection.",
                RuleKind.Conflict);
        TenantComment = Guard.Optional(comment, "comment", CommentMax);
        TenantAcknowledgedAt = now;
    }

    private InspectionItem Item(Guid id) =>
        _items.FirstOrDefault(i => i.Id == id)
        ?? throw new DomainRuleException("inspection.item_not_found", "That area isn't on this checklist.");

    private void RequireDraft()
    {
        if (Status != InspectionStatus.Draft)
            throw new DomainRuleException("inspection.completed", "A completed inspection can't be changed.",
                RuleKind.Conflict);
    }
}

public sealed class InspectionItem : Entity, IAuditable
{
    private readonly List<InspectionPhoto> _photos = [];

    private InspectionItem() { } // EF Core

    internal InspectionItem(string area, int order)
    {
        Area = area;
        Order = order;
    }

    public Guid InspectionId { get; private set; }
    public string Area { get; private set; } = string.Empty;
    public int Order { get; private set; }

    /// <summary>Null until the area has been checked.</summary>
    public ItemCondition? Condition { get; private set; }

    public string? Notes { get; private set; }
    public IReadOnlyCollection<InspectionPhoto> Photos => _photos.AsReadOnly();

    internal void Update(ItemCondition? condition, string? notes)
    {
        Condition = condition;
        Notes = notes;
    }

    internal InspectionPhoto AddPhoto(StoredFile file, DateTimeOffset now)
    {
        if (_photos.Any(p => p.Sha256 == file.Sha256))
            throw new DomainRuleException("inspection.duplicate_photo", "This photo has already been added here.",
                RuleKind.Conflict);
        var photo = new InspectionPhoto(file, now);
        _photos.Add(photo);
        return photo;
    }
}

public sealed class InspectionPhoto : Entity, IAuditable
{
    private InspectionPhoto() { } // EF Core

    internal InspectionPhoto(StoredFile file, DateTimeOffset at)
    {
        FileKey = file.Key;
        FileName = file.FileName.Length > 120 ? file.FileName[..120] : file.FileName;
        ContentType = file.ContentType;
        SizeBytes = file.SizeBytes;
        Sha256 = file.Sha256;
        At = at;
    }

    public Guid InspectionItemId { get; private set; }
    public string FileKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public DateTimeOffset At { get; private set; }
}
