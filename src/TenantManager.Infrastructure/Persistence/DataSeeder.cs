using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Leases;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Properties;
using TenantManager.Domain.Settings;
using TenantManager.Domain.Tenants;
using TenantManager.Infrastructure.Identity;

namespace TenantManager.Infrastructure.Persistence;

public sealed class SeedOptions
{
    public const string Section = "Seed";

    /// <summary>Create the synthetic demo data below. Off by default; docker-compose turns it on.</summary>
    public bool DemoData { get; set; }

    /// <summary>Password for every demo login. Comes from .env, never from git.</summary>
    public string? Password { get; set; }
}

/// <summary>
/// Applies migrations, creates the three roles, and optionally loads synthetic demo data.
/// Safe to run repeatedly: each step checks whether its data already exists.
/// All names, emails (@example.com, @mastene.test) and phone numbers are made up.
/// </summary>
public sealed class DataSeeder(
    AppDbContext db,
    UserManager<AppUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IClock clock,
    LeaseDocumentFactory leaseDocuments,
    ILogger<DataSeeder> logger)
{
    public const string AdminEmail = "admin@mastene.test";
    public const string LandlordEmail = "landlord@mastene.test";

    public async Task MigrateAsync(CancellationToken ct)
    {
        logger.LogInformation("Applying database migrations");
        await db.Database.MigrateAsync(ct);
    }

    public async Task SeedAsync(SeedOptions options, CancellationToken ct)
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role));
        }

        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.Add(BusinessSettings.CreateDefault());
            await db.SaveChangesAsync(ct);
        }

        if (!options.DemoData) return;
        if (string.IsNullOrWhiteSpace(options.Password))
        {
            logger.LogWarning("Seed:DemoData is on but Seed:Password is empty; skipping demo data");
            return;
        }
        if (await users.FindByEmailAsync(LandlordEmail) is not null)
        {
            logger.LogInformation("Demo data already present");
            return;
        }

        logger.LogInformation("Seeding synthetic demo data");
        await CreateUserAsync(AdminEmail, "Ayanda Admin", Roles.Admin, options.Password, null, null);
        var landlord = await CreateUserAsync(LandlordEmail, "Thandi Nkosi", Roles.Landlord, options.Password, null, null);

        var jacaranda = new Property(landlord.Id, "Jacaranda Court",
            new PropertyAddress("14 Jacaranda Street", null, "Pretoria", "Gauteng", "0083"));
        jacaranda.AddUnit("Flat 1", 6500m, 6500m);
        jacaranda.AddUnit("Flat 2", 7200m, 7200m);
        jacaranda.AddUnit("Flat 3", 7200m, 7200m);

        var marula = new Property(landlord.Id, "Marula Gardens",
            new PropertyAddress("3 Marula Close", "Noordwyk", "Midrand", "Gauteng", "1685"));
        marula.AddUnit("Cottage A", 5800m, 5800m);
        marula.AddUnit("Cottage B", 5800m, 5800m);

        var tenants = new[]
        {
            new Tenant(landlord.Id, "Sipho", "Dlamini", "sipho.dlamini@example.com", "082 555 0101"),
            new Tenant(landlord.Id, "Lerato", "Mokoena", "lerato.mokoena@example.com", "083 555 0102"),
            new Tenant(landlord.Id, "Johan", "van der Merwe", "johan.vdmerwe@example.com", "084 555 0103"),
        };

        db.Properties.AddRange(jacaranda, marula);
        db.Tenants.AddRange(tenants);
        await db.SaveChangesAsync(ct);

        // The first two tenants get logins so the tenant view can be tried out.
        var tenantUsers = new List<AppUser>();
        foreach (var tenant in tenants.Take(2))
            tenantUsers.Add(await CreateUserAsync(tenant.Email, tenant.FullName, Roles.Tenant, options.Password,
                tenant.Id, landlord.Id));

        // Two signed leases: Sipho's started eight months ago; Lerato's has about a month left, so the
        // lifecycle run below flags it Expiring. Johan has no lease, so he can be given one in the demo.
        var today = clock.Today();
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        await SeedSignedLeaseAsync(jacaranda.Units.First(u => u.Label == "Flat 1"), tenants[0], tenantUsers[0],
            landlord, firstOfMonth.AddMonths(-8), escalationPct: 8m, ct);
        await SeedSignedLeaseAsync(marula.Units.First(u => u.Label == "Cottage A"), tenants[1], tenantUsers[1],
            landlord, firstOfMonth.AddMonths(-11), escalationPct: 6m, ct);

        var inForce = LeaseTerm.InForce;
        var leases = await db.Leases.Where(l => inForce.Contains(l.Status))
            .Include(l => l.Unit).Include(l => l.Previous).Include(l => l.Signatures).Include(l => l.ExpiryNotices)
            .ToListAsync(ct);
        LeaseLifecycle.Run(leases, today, LeaseSettings.Defaults);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSignedLeaseAsync(Unit unit, Tenant tenant, AppUser tenantUser, AppUser landlord,
        DateOnly start, decimal escalationPct, CancellationToken ct)
    {
        var settings = LeaseSettings.Defaults;
        var today = clock.Today();
        var lease = Lease.CreateDraft(await db.NextLeaseNumberAsync(today, ct), unit, tenant, start,
            unit.RentAmount, unit.DepositAmount, escalationPct, settings);
        db.Leases.Add(lease);
        await db.SaveChangesAsync(ct); // the lease id is part of the document's storage key

        var unsigned = await leaseDocuments.CreateAsync(lease, LeaseDocumentKind.Unsigned, landlord.DisplayName,
            settings.TemplateVersion, ct);
        lease.Generate(landlord.DisplayName, settings.TemplateVersion, unsigned);

        var signedAt = clock.UtcNow;
        lease.Sign(LeaseParty.Landlord, landlord.Id, landlord.DisplayName, "127.0.0.1", signedAt, today);
        lease.Sign(LeaseParty.Tenant, tenantUser.Id, tenantUser.DisplayName, "127.0.0.1", signedAt, today);
        lease.AddSignedDocument(await leaseDocuments.CreateAsync(lease, LeaseDocumentKind.Signed, landlord.DisplayName,
            settings.TemplateVersion, ct));
        await db.SaveChangesAsync(ct);
    }

    private async Task<AppUser> CreateUserAsync(string email, string name, string role, string password,
        Guid? tenantId, Guid? ownerId)
    {
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = name,
            TenantId = tenantId,
            OwnerId = ownerId,
            CreatedAt = clock.UtcNow,
        };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new InvalidOperationException(
                $"Could not create {email}: {string.Join("; ", created.Errors.Select(e => e.Description))}");
        await users.AddToRoleAsync(user, role);
        return user;
    }
}
