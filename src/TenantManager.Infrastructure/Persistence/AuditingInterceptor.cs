using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Common;

namespace TenantManager.Infrastructure.Persistence;

/// <summary>
/// Runs inside every SaveChanges, before anything is written:
/// 1. stamps Created/Updated fields on every entity;
/// 2. adds one AuditLog row per changed <see cref="IAuditable"/> entity (BR20).
/// Because the audit rows are added to the same SaveChanges they commit in the same
/// transaction as the change itself, so the audit can never miss or invent a change.
/// </summary>
public sealed class AuditingInterceptor(ICurrentUser currentUser, IClock clock, AuditRedaction redaction) : SaveChangesInterceptor
{
    private static readonly HashSet<string> SkippedFields =
    [
        nameof(Entity.CreatedAt), nameof(Entity.CreatedBy), nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy),
        "RowVersion",
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Process(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Process(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Process(DbContext? context)
    {
        if (context is null) return;

        var now = clock.UtcNow;
        var userId = currentUser.UserId;
        var logs = new List<AuditLog>();

        // Entries() runs DetectChanges first, so foreign keys and modified flags are current.
        foreach (var entry in context.ChangeTracker.Entries<Entity>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(e => e.CreatedAt).CurrentValue = now;
                    entry.Property(e => e.CreatedBy).CurrentValue = userId;
                    break;
                case EntityState.Modified:
                    entry.Property(e => e.UpdatedAt).CurrentValue = now;
                    entry.Property(e => e.UpdatedBy).CurrentValue = userId;
                    break;
            }

            if (entry.Entity is IAuditable && BuildLog(entry, now, userId) is { } log)
                logs.Add(log);
        }

        if (logs.Count > 0) context.Set<AuditLog>().AddRange(logs);
    }

    private AuditLog? BuildLog(EntityEntry<Entity> entry, DateTimeOffset now, Guid? userId)
    {
        if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) return null;

        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();

        foreach (var p in entry.Properties)
        {
            var name = p.Metadata.Name;
            if (SkippedFields.Contains(name)) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    after[name] = p.CurrentValue;
                    break;
                case EntityState.Modified when p.IsModified && !Equals(p.OriginalValue, p.CurrentValue):
                    before[name] = redaction.Active && p.Metadata.ClrType == typeof(string) ? AuditRedaction.Erased : p.OriginalValue;
                    after[name] = p.CurrentValue;
                    break;
                case EntityState.Deleted:
                    before[name] = p.OriginalValue;
                    break;
            }
        }

        if (entry.State == EntityState.Modified && after.Count == 0) return null;

        var softDeleted = entry.State == EntityState.Modified &&
                          after.TryGetValue(nameof(ISoftDeletable.IsDeleted), out var isDeleted) &&
                          isDeleted is true;

        var action = entry.State switch
        {
            EntityState.Added => AuditActions.Created,
            EntityState.Deleted => AuditActions.Deleted,
            _ when softDeleted => AuditActions.Deleted,
            _ => AuditActions.Updated,
        };

        return new AuditLog(
            now, userId, entry.Metadata.ClrType.Name, entry.Entity.Id.ToString(), action,
            before.Count > 0 ? JsonSerializer.Serialize(before, Json) : null,
            after.Count > 0 ? JsonSerializer.Serialize(after, Json) : null,
            currentUser.CorrelationId);
    }
}
