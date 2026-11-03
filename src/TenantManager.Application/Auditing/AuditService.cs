using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;

namespace TenantManager.Application.Auditing;

public sealed record AuditLogDto(
    long Id, DateTimeOffset OccurredAt, Guid? UserId, string EntityType, string EntityId, string Action,
    string? BeforeJson, string? AfterJson, string? CorrelationId);

public sealed record AuditListQuery : PagedQuery
{
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

/// <summary>Read-only access to the audit trail, newest first.</summary>
public sealed class AuditService(IApplicationDbContext db)
{
    public Task<PagedResult<AuditLogDto>> ListAsync(AuditListQuery query, CancellationToken ct)
    {
        var q = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.EntityType)) q = q.Where(a => a.EntityType == query.EntityType);
        if (!string.IsNullOrWhiteSpace(query.EntityId)) q = q.Where(a => a.EntityId == query.EntityId);
        if (query.From is { } from) q = q.Where(a => a.OccurredAt >= from);
        if (query.To is { } to) q = q.Where(a => a.OccurredAt < to);

        return q.OrderByDescending(a => a.Id).ToPagedAsync(query, a => new AuditLogDto(
            a.Id, a.OccurredAt, a.UserId, a.EntityType, a.EntityId, a.Action, a.BeforeJson, a.AfterJson,
            a.CorrelationId), ct);
    }
}
