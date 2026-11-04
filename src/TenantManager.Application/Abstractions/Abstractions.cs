using Microsoft.EntityFrameworkCore;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Inspections;
using TenantManager.Domain.Issues;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Messaging;
using TenantManager.Domain.Payments;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Settings;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Abstractions;

/// <summary>
/// The slice of the database the use cases need. Application references only the
/// EF Core abstractions package, not SQL Server, so the provider stays an Infrastructure detail.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Property> Properties { get; }
    DbSet<Unit> Units { get; }
    DbSet<Tenant> Tenants { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Lease> Leases { get; }
    DbSet<BusinessSettings> Settings { get; }
    DbSet<RentCharge> RentCharges { get; }
    DbSet<ProofOfPayment> Payments { get; }
    DbSet<Deposit> Deposits { get; }
    DbSet<Issue> Issues { get; }
    DbSet<Contractor> Contractors { get; }
    DbSet<Inspection> Inspections { get; }
    DbSet<ConsentRecord> Consents { get; }
    DbSet<Conversation> Conversations { get; }
    DbSet<Message> Messages { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Next lease number, e.g. L2026-0007, from a database sequence (never reused).</summary>
    Task<string> NextLeaseNumberAsync(DateOnly today, CancellationToken cancellationToken);

    /// <summary>Next receipt number, e.g. R2026-0042, from a database sequence.</summary>
    Task<string> NextReceiptNumberAsync(DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction holding exclusive locks on the given keys (e.g.
    /// "unit:{id}"), so two requests can't both pass a check-then-write rule such as "no overlapping
    /// leases" (BR03). The work may be run again on a transient database error, so it must load
    /// everything it changes itself.
    /// </summary>
    Task<T> InLockedTransactionAsync<T>(IEnumerable<string> lockKeys, Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Landlord = "Landlord";
    public const string Tenant = "Tenant";
    public static readonly IReadOnlyList<string> All = [Admin, Landlord, Tenant];
}

/// <summary>Who is making the request, taken from the validated JWT.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Role { get; }

    /// <summary>The landlord whose data this user works with (the landlord's own id, or a tenant's landlord).</summary>
    Guid? OwnerId { get; }

    /// <summary>Set only for users in the Tenant role.</summary>
    Guid? TenantId { get; }

    string? CorrelationId { get; }

    /// <summary>The user's display name, from the token.</summary>
    string? DisplayName { get; }

    /// <summary>Client IP address, recorded with signatures.</summary>
    string? IpAddress { get; }
}

public static class CurrentUserExtensions
{
    public static Guid RequireUserId(this ICurrentUser user) =>
        user.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");

    public static Guid RequireOwnerId(this ICurrentUser user) =>
        user.OwnerId ?? throw new UnauthorizedAccessException("The current user is not linked to a landlord.");

    public static bool IsTenant(this ICurrentUser user) => user.Role == Roles.Tenant;
}

/// <summary>All business rules read time from here, never DateTime.Now, so tests can freeze it.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public static class BusinessTime
{
    /// <summary>All business dates (rent due, lease start and end) are South African dates.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    public static DateOnly Today(this IClock clock) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, Zone).DateTime);
}

/// <summary>Stores documents: lease PDFs, receipts, proof of payment and photos. ADR-0005.</summary>
public interface IFileStorage
{
    /// <summary>Writes a new file. Keys are never reused, so an existing key is an error.</summary>
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <returns>Null when there is no file with this key.</returns>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Answers "does this tenant still have a lease or owe money?" for BR19.
/// Leases are checked from Phase 2; the balance is always zero until the ledger arrives in Phase 3.
/// </summary>
public interface ITenantObligationsProvider
{
    Task<Domain.Tenants.TenantObligations> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Set for one unit of work that erases personal data (POPIA). The audit log then records which fields
/// changed and that they were erased, but not their old values, so erasing doesn't copy the very data
/// being erased into the append-only audit log.
/// </summary>
public sealed class AuditRedaction
{
    public const string Erased = "[erased]";
    public bool Active { get; set; }
}

/// <summary>
/// Feature flags (configuration "Features:{name}", on unless set to false), so a risky feature can be
/// switched off in production by an app setting, without a deploy (ADR-0017).
/// </summary>
public interface IFeatureFlags
{
    bool IsEnabled(string feature);
}

public static class Features
{
    /// <summary>Off: WhatsApp messages stay queued (nothing is sent) until it is switched on again.</summary>
    public const string WhatsAppSending = "WhatsAppSending";

    /// <summary>Off: the daily rent, lease and renewal reminders aren't queued.</summary>
    public const string Reminders = "Reminders";
}

/// <summary>Business metrics, exported with OpenTelemetry (ADR-0018). Names follow OTel conventions.</summary>
public static class AppTelemetry
{
    public const string Name = "TenantManager";
    public static readonly System.Diagnostics.Metrics.Meter Meter = new(Name);

    public static readonly System.Diagnostics.Metrics.Counter<long> PaymentsVerified =
        Meter.CreateCounter<long>("tm.payments.verified", description: "Proofs of payment verified");
    public static readonly System.Diagnostics.Metrics.Counter<long> IssuesOpened =
        Meter.CreateCounter<long>("tm.issues.opened", description: "Maintenance issues logged");
    public static readonly System.Diagnostics.Metrics.Counter<long> IssuesEscalated =
        Meter.CreateCounter<long>("tm.issues.escalated", description: "Issues that missed a target");
    public static readonly System.Diagnostics.Metrics.Counter<long> MessagesSent =
        Meter.CreateCounter<long>("tm.messages.sent", description: "WhatsApp messages accepted by the provider");
    public static readonly System.Diagnostics.Metrics.Counter<long> MessagesFailed =
        Meter.CreateCounter<long>("tm.messages.failed", description: "WhatsApp send attempts that failed");
    public static readonly System.Diagnostics.Metrics.Counter<long> WebhooksRejected =
        Meter.CreateCounter<long>("tm.webhooks.rejected", description: "WhatsApp webhook calls with a bad signature");
}
