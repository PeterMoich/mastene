using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Auditing;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Settings;
using TenantManager.Domain.Tenants;
using TenantManager.Infrastructure.Identity;

namespace TenantManager.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IApplicationDbContext
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<BusinessSettings> Settings => Set<BusinessSettings>();
    public DbSet<RentCharge> RentCharges => Set<RentCharge>();
    public DbSet<ProofOfPayment> Payments => Set<ProofOfPayment>();
    public DbSet<Deposit> Deposits => Set<Deposit>();

    public async Task<string> NextReceiptNumberAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var values = await Database
            .SqlQuery<long>($"SELECT NEXT VALUE FOR [dbo].[ReceiptNumbers] AS [Value]")
            .ToListAsync(cancellationToken);
        return $"R{today.Year}-{values.Single():D4}";
    }

    public async Task<string> NextLeaseNumberAsync(DateOnly today, CancellationToken cancellationToken)
    {
        // Not composed with LINQ: SQL Server doesn't allow NEXT VALUE FOR inside a derived table.
        var values = await Database
            .SqlQuery<long>($"SELECT NEXT VALUE FOR [dbo].[LeaseNumbers] AS [Value]")
            .ToListAsync(cancellationToken);
        return $"L{today.Year}-{values.Single():D4}";
    }

    public async Task<T> InLockedTransactionAsync<T>(IEnumerable<string> lockKeys,
        Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        // Sorted so two requests that need the same locks always take them in the same order (no deadlock).
        var keys = lockKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var attempt = 0;
        var strategy = Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) ChangeTracker.Clear(); // a retry starts from fresh data
            await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
            foreach (var key in keys)
            {
                try
                {
                    await Database.ExecuteSqlInterpolatedAsync($"""
                        DECLARE @result int;
                        EXEC @result = sp_getapplock @Resource = {key}, @LockMode = 'Exclusive',
                            @LockOwner = 'Transaction', @LockTimeout = 15000;
                        IF @result < 0 THROW 51001, N'Timed out waiting for a lock.', 1;
                        """, cancellationToken);
                }
                catch (SqlException ex) when (ex.Number == 51001)
                {
                    throw new DomainRuleException("busy",
                        "Someone else is changing this record right now. Please try again.", RuleKind.Conflict);
                }
            }

            var result = await work(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        builder.HasSequence<long>("LeaseNumbers").StartsAt(1);
        builder.HasSequence<long>("ReceiptNumbers").StartsAt(1);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All money is ZAR with cents. One convention instead of repeating it per column.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    /// <summary>
    /// Turns unique-index violations into a 409 instead of a 500. The services check for
    /// duplicates first; this catches the race where two requests pass that check together.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainRuleException("concurrency",
                "This record was changed by someone else. Reload it and try again.", RuleKind.Conflict);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new DomainRuleException("duplicate",
                "This record conflicts with an existing one (a value that must be unique is already used).",
                RuleKind.Conflict);
        }
    }
}
