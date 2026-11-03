namespace TenantManager.Domain.Common;

/// <summary>
/// Base class for everything with an identity. The Id is left empty until EF Core tracks the
/// entity; EF then assigns a SQL Server–friendly sequential GUID, which keeps the clustered
/// index from fragmenting the way random GUIDs would.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; private set; }

    /// <summary>Set by the persistence layer when the row is first saved.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

/// <summary>Marker: every insert, update and soft delete of this entity is written to the audit log.</summary>
public interface IAuditable;

/// <summary>Entities that belong to a landlord. Kept so the app can grow to several landlords later (ADR-007).</summary>
public interface IOwned
{
    Guid OwnerId { get; }
}

/// <summary>Rows are flagged, never physically removed. A global query filter hides them.</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTimeOffset? DeletedAt { get; }
    Guid? DeletedBy { get; }
}
