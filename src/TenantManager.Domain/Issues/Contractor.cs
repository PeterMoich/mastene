using TenantManager.Domain.Common;
using TenantManager.Domain.Tenants;

namespace TenantManager.Domain.Issues;

/// <summary>Someone the landlord sends to fix things. A simple record, not a login (Phase 0 decision).</summary>
public sealed class Contractor : Entity, IOwned, IAuditable, ISoftDeletable
{
    public const int NameMax = 120;
    public const int TradeMax = 60;
    public const int EmailMax = 254;
    public const int NotesMax = 500;

    private Contractor() { } // EF Core

    public Contractor(Guid ownerId, string name, string trade, string phone, string? email, string? notes)
    {
        OwnerId = ownerId;
        Update(name, trade, phone, email, notes);
    }

    public Guid OwnerId { get; private set; }
    public string Name { get; private set; } = string.Empty;

    /// <summary>What they do, e.g. "Plumber".</summary>
    public string Trade { get; private set; } = string.Empty;

    /// <summary>E.164, e.g. +27821234567.</summary>
    public string Phone { get; private set; } = string.Empty;

    public string? Email { get; private set; }
    public string? Notes { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public void Update(string name, string trade, string phone, string? email, string? notes)
    {
        Name = Guard.NotBlank(name, "name", NameMax);
        Trade = Guard.NotBlank(trade, "trade", TradeMax);
        Phone = Tenant.NormalisePhone(phone);
        Email = Guard.Optional(email, "email", EmailMax)?.ToLowerInvariant();
        Notes = Guard.Optional(notes, "notes", NotesMax);
    }

    /// <summary>Soft delete. Not while they are assigned to an issue that is still being worked on.</summary>
    public void Delete(int unfinishedIssues, Guid deletedBy, DateTimeOffset at)
    {
        if (IsDeleted) return;
        if (unfinishedIssues > 0)
            throw new DomainRuleException("contractor.has_open_issues",
                $"This contractor is assigned to {unfinishedIssues} issue(s) not yet resolved. Reassign them first.",
                RuleKind.Conflict);
        IsDeleted = true;
        DeletedAt = at;
        DeletedBy = deletedBy;
    }
}
