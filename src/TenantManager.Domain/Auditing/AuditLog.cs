namespace TenantManager.Domain.Auditing;

public static class AuditActions
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
}

/// <summary>
/// One row per changed entity per save (ADR-008). Append-only: the application has no update
/// or delete path, and a database trigger rejects UPDATE and DELETE on the table.
/// </summary>
public sealed class AuditLog
{
    private AuditLog() { } // EF Core

    public AuditLog(DateTimeOffset occurredAt, Guid? userId, string entityType, string entityId,
        string action, string? beforeJson, string? afterJson, string? correlationId)
    {
        OccurredAt = occurredAt;
        UserId = userId;
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        BeforeJson = beforeJson;
        AfterJson = afterJson;
        CorrelationId = correlationId;
    }

    public long Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Null for changes made by the system (seeding, background jobs).</summary>
    public Guid? UserId { get; private set; }

    public string EntityType { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;

    /// <summary>Changed fields only, before the change. Null on create.</summary>
    public string? BeforeJson { get; private set; }

    /// <summary>Changed fields only, after the change.</summary>
    public string? AfterJson { get; private set; }

    /// <summary>The request trace id, so all rows from one request can be grouped.</summary>
    public string? CorrelationId { get; private set; }
}
