using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenantManager.Application.Abstractions;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TenantManager.Application.Leases;
using TenantManager.Domain.Inspections;
using TenantManager.Domain.Issues;
using TenantManager.Application.Payments;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Messaging;
using TenantManager.Domain.Payments;
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

    /// <summary>
    /// Show the demo logins (landlord and two tenants, not the admin) on the sign-in page, for a public
    /// demo site. Only works together with DemoData and a Password. Anyone who opens the site can then
    /// sign in and change the demo data, so never turn it on where real people's data lives.
    /// </summary>
    public bool ShowDemoLogins { get; set; }
}

/// <summary>A demo account offered on the sign-in page.</summary>
public sealed record DemoLogin(string Role, string Name, string Email);

/// <summary>What the sign-in page shows when <see cref="SeedOptions.ShowDemoLogins"/> is on.</summary>
public sealed record DemoLoginsResponse(string Password, IReadOnlyList<DemoLogin> Accounts)
{
    /// <summary>The demo accounts the seeder creates. The admin is left out so visitors can't lock the others.</summary>
    public static readonly IReadOnlyList<DemoLogin> SeededAccounts =
    [
        new("Landlord", "Thandi Nkosi", DataSeeder.LandlordEmail),
        new("Tenant", "Sipho Dlamini", "sipho.dlamini@example.com"),
        new("Tenant", "Lerato Mokoena", "lerato.mokoena@example.com"),
    ];

    /// <summary>The response, or null when demo logins must not be shown.</summary>
    public static DemoLoginsResponse? For(SeedOptions options) =>
        options is { DemoData: true, ShowDemoLogins: true } && !string.IsNullOrWhiteSpace(options.Password)
            ? new DemoLoginsResponse(options.Password, SeededAccounts)
            : null;
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
    RentService rent,
    ReceiptFactory receipts,
    IFileStorage files,
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
        if (await users.FindByEmailAsync(LandlordEmail) is { } existingLandlord)
        {
            logger.LogInformation("Demo data already present");
            // Databases seeded before Phase 4 get the maintenance demo data added once.
            if (!await db.Contractors.IgnoreQueryFilters().AnyAsync(ct))
                await SeedMaintenanceAsync(existingLandlord, ct);
            if (!await db.Consents.AnyAsync(ct))
                await SeedMessagingAsync(ct);
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
        var siphoLease = await SeedSignedLeaseAsync(jacaranda.Units.First(u => u.Label == "Flat 1"), tenants[0],
            tenantUsers[0], landlord, firstOfMonth.AddMonths(-8), escalationPct: 8m, ct);
        var leratoLease = await SeedSignedLeaseAsync(marula.Units.First(u => u.Label == "Cottage A"), tenants[1],
            tenantUsers[1], landlord, firstOfMonth.AddMonths(-11), escalationPct: 6m, ct);

        var inForce = LeaseTerm.InForce;
        var leases = await db.Leases.Where(l => inForce.Contains(l.Status))
            .Include(l => l.Unit).Include(l => l.Previous).Include(l => l.Signatures).Include(l => l.ExpiryNotices)
            .ToListAsync(ct);
        LeaseLifecycle.Run(leases, today, LeaseSettings.Defaults);
        await db.SaveChangesAsync(ct);

        // Rent history. Sipho is paid up except this month, and his proof for it is waiting for review.
        // Lerato missed most of last month and all of this month, so she shows in arrears.
        await rent.RaiseChargesAsync(null, ct);
        await SeedPaymentsAsync(siphoLease, tenantUsers[0], landlord, unpaidMonths: 1, partialOnFirstUnpaid: null,
            submitForLatest: true, ct);
        await SeedPaymentsAsync(leratoLease, tenantUsers[1], landlord, unpaidMonths: 2, partialOnFirstUnpaid: 3000m,
            submitForLatest: false, ct);
        await rent.RecordOverdueNoticesAsync(ct);
        await SeedMaintenanceAsync(landlord, ct);
        await SeedMessagingAsync(ct);
    }

    /// <summary>
    /// Sipho agreed to WhatsApp messages and wrote two hours ago, so his thread is inside the 24-hour
    /// window. Lerato hasn't agreed, so she gets no WhatsApp messages; Johan has no conversation yet.
    /// </summary>
    private async Task SeedMessagingAsync(CancellationToken ct)
    {
        var sipho = await db.Tenants.FirstOrDefaultAsync(t => t.Email == "sipho.dlamini@example.com", ct);
        if (sipho is null) return;
        logger.LogInformation("Seeding synthetic messaging demo data");
        var siphoUser = await users.FindByEmailAsync(sipho.Email);
        var now = clock.UtcNow;

        db.Consents.Add(ConsentRecord.Grant(sipho.OwnerId, sipho.Id, sipho.Phone, ConsentSource.Tenant, sipho.FullName,
            siphoUser?.Id, null, now.AddDays(-60)));
        var conversation = new Conversation(sipho.OwnerId, sipho.Id);
        db.Conversations.Add(conversation);

        var lastPayment = await db.Payments.Where(p => p.TenantId == sipho.Id && p.ReceiptNumber != null)
            .OrderByDescending(p => p.ReviewedAt).FirstOrDefaultAsync(ct);
        var thread = new List<Message>();
        if (lastPayment is not null)
        {
            var receipt = Message.OutboundTemplate(conversation.Id, MessageTemplates.PaymentReceived.Render(
                    [sipho.FirstName, Application.Messaging.MessageFormat.Money(lastPayment.Amount), lastPayment.ReceiptNumber!]),
                now.AddDays(-5), Application.Messaging.Notifier.SystemName, $"payment-received:{lastPayment.Id}");
            receipt.BeginAttempt(now.AddDays(-5));
            receipt.MarkSent($"wamid.SEED{Guid.NewGuid():N}", now.AddDays(-5));
            receipt.ApplyProviderStatus(MessageStatus.Read, null);
            thread.Add(receipt);
        }
        thread.Add(Message.InboundWhatsApp(conversation.Id,
            "Hi Thandi, the plumber didn't come this morning. Can he come tomorrow instead?",
            $"wamid.SEED{Guid.NewGuid():N}", now.AddHours(-2), sipho.FullName));
        foreach (var m in thread)
        {
            db.Messages.Add(m);
            conversation.Touch(m);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Contractors, issues at different stages (one escalated, one waiting for the tenant to confirm the
    /// fix) and completed move-in inspections, so every Phase 4 screen has something to show.
    /// </summary>
    private async Task SeedMaintenanceAsync(AppUser landlord, CancellationToken ct)
    {
        var sipho = await db.Leases.Include(l => l.Tenant).Include(l => l.Unit)
            .FirstOrDefaultAsync(l => l.Tenant.Email == "sipho.dlamini@example.com" && l.Status == LeaseStatus.Active, ct);
        var lerato = await db.Leases.Include(l => l.Tenant).Include(l => l.Unit)
            .Where(l => l.Tenant.Email == "lerato.mokoena@example.com")
            .Where(l => l.Status == LeaseStatus.Active || l.Status == LeaseStatus.Expiring).FirstOrDefaultAsync(ct);
        var flat2 = await db.Units.Include(u => u.Property)
            .FirstOrDefaultAsync(u => u.Label == "Flat 2" && u.Property.Name == "Jacaranda Court", ct);
        if (sipho is null || lerato is null || flat2 is null)
        {
            logger.LogWarning("Demo leases not found; skipping maintenance demo data");
            return;
        }
        logger.LogInformation("Seeding synthetic maintenance demo data");

        var siphoUser = await users.FindByEmailAsync(sipho.Tenant.Email);
        var leratoUser = await users.FindByEmailAsync(lerato.Tenant.Email);
        var owner = new IssueActor(landlord.Id, landlord.DisplayName, IsTenant: false);
        var siphoActor = new IssueActor(siphoUser!.Id, siphoUser.DisplayName, IsTenant: true);
        var leratoActor = new IssueActor(leratoUser!.Id, leratoUser.DisplayName, IsTenant: true);
        var targets = SlaTargets.Defaults;
        var now = clock.UtcNow;

        var plumber = new Contractor(landlord.Id, "Bongani Mthembu Plumbing", "Plumber", "082 555 0201",
            "bongani.plumbing@example.com", "Prefers WhatsApp. Call-out fee agreed in advance.");
        var electrician = new Contractor(landlord.Id, "Spark Bright Electrical", "Electrician", "083 555 0202",
            "office@sparkbright.example.com", "Registered electrician; can issue a certificate of compliance.");
        var gardener = new Contractor(landlord.Id, "Groen Garden Care", "Gardener", "084 555 0203", null, null);
        db.Contractors.AddRange(plumber, electrician, gardener);
        await db.SaveChangesAsync(ct);

        // 1. In progress with the plumber, inside its targets.
        var tap = new Issue(sipho.OwnerId, sipho.UnitId, sipho.Id, sipho.TenantId, IssueCategory.Plumbing,
            IssuePriority.Medium, "Kitchen tap leaking",
            "The cold tap in the kitchen drips all the time, even when closed tightly. There is a small puddle under the sink.",
            siphoActor, targets, now.AddDays(-3));
        tap.Acknowledge(owner, now.AddDays(-3).AddHours(2));
        tap.Start(owner, now.AddDays(-2));
        tap.AssignContractor(plumber.Id, plumber.Name, owner, now.AddDays(-2));
        tap.AddComment("Bongani will come on Thursday morning between 8 and 10.", owner, now.AddDays(-2));
        tap.AddComment("Thanks, I'll be home.", siphoActor, now.AddDays(-2).AddHours(1));
        tap.AddPhoto(await StoreDemoPhotoAsync("issues", "kitchen-tap.png", 70, 130, 180, ct), siphoActor, now.AddDays(-3));

        // 2. Not acknowledged, past its 24-hour response target: escalated (BR15).
        var geyser = new Issue(lerato.OwnerId, lerato.UnitId, lerato.Id, lerato.TenantId, IssueCategory.Plumbing,
            IssuePriority.High, "Geyser not heating",
            "There has been no hot water since yesterday evening. The geyser light is off and the trip switch keeps tripping.",
            leratoActor, targets, now.AddHours(-30));
        geyser.CheckTargets(now);

        // 3. Fixed and confirmed by the tenant.
        var light = new Issue(sipho.OwnerId, sipho.UnitId, sipho.Id, sipho.TenantId, IssueCategory.Electrical,
            IssuePriority.Urgent, "Bedroom light switch sparks",
            "The light switch in the main bedroom sparks when I switch it on. I've stopped using it.",
            siphoActor, targets, now.AddDays(-20));
        light.Acknowledge(owner, now.AddDays(-20).AddHours(1));
        light.AssignContractor(electrician.Id, electrician.Name, owner, now.AddDays(-20).AddHours(1));
        light.Start(owner, now.AddDays(-20).AddHours(3));
        light.Resolve("Switch and back box replaced; circuit tested.", owner, now.AddDays(-20).AddHours(9));
        light.Close(siphoActor, now.AddDays(-19));

        // 4. Resolved, waiting for the tenant to confirm or reopen.
        var gate = new Issue(lerato.OwnerId, lerato.UnitId, lerato.Id, lerato.TenantId, IssueCategory.Security,
            IssuePriority.Medium, "Gate remote not working",
            "My gate remote stopped opening the main gate. I replaced the battery but it still doesn't work.",
            leratoActor, targets, now.AddDays(-4));
        gate.Start(owner, now.AddDays(-4).AddHours(5));
        gate.Resolve("Remote re-coded to the gate motor. Please check it works.", owner, now.AddDays(-2));

        // 5. The landlord's own job on the vacant flat (no tenant).
        var paint = new Issue(landlord.Id, flat2.Id, null, null, IssueCategory.Structural, IssuePriority.Low,
            "Paint touch-up before next tenant", "Touch up scuffed walls in the lounge and passage before viewings.",
            owner, targets, now.AddDays(-2));

        db.Issues.AddRange(tap, geyser, light, gate, paint);
        await db.SaveChangesAsync(ct);

        // Move-in inspections: Sipho's acknowledged; Lerato's waiting for her acknowledgement.
        await SeedMoveInAsync(sipho, landlord, acknowledged: true, ct);
        await SeedMoveInAsync(lerato, landlord, acknowledged: false, ct);
    }

    private async Task SeedMoveInAsync(Lease lease, AppUser landlord, bool acknowledged, CancellationToken ct)
    {
        var activated = lease.ActivatedOn ?? lease.StartDate;
        var inspection = Inspection.StartMoveIn(lease, activated);
        db.Inspections.Add(inspection);
        await db.SaveChangesAsync(ct); // item ids are needed below

        foreach (var item in inspection.Items)
        {
            var (condition, notes) = item.Area switch
            {
                "Bathroom" => (ItemCondition.Fair, "Chipped tile next to the bath."),
                "Walls and ceilings" => (ItemCondition.Good, "Freshly painted."),
                "Keys, remotes and access tags" => (ItemCondition.Good, "2 door keys, 1 gate remote, 1 tag."),
                _ => (ItemCondition.Good, (string?)null),
            };
            inspection.UpdateItem(item.Id, condition, notes);
        }
        var lounge = inspection.Items.First(i => i.Area == "Lounge");
        inspection.AddPhoto(lounge.Id, await StoreDemoPhotoAsync("inspections", "lounge.png", 200, 170, 120, ct),
            clock.UtcNow);
        var bathroom = inspection.Items.First(i => i.Area == "Bathroom");
        inspection.AddPhoto(bathroom.Id, await StoreDemoPhotoAsync("inspections", "bathroom-tile.png", 150, 190, 200, ct),
            clock.UtcNow);
        inspection.Complete(landlord.DisplayName, clock.UtcNow);
        if (acknowledged) inspection.Acknowledge(null, clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }

    private async Task<StoredFile> StoreDemoPhotoAsync(string folder, string name, byte r, byte g, byte b,
        CancellationToken ct)
    {
        // A random tint keeps every demo photo's hash different.
        var png = DemoImages.Png(r, g, (byte)(b ^ RandomNumberGenerator.GetInt32(0, 16)));
        var key = $"{folder}/demo/{Guid.NewGuid():N}.png";
        using (var stream = new MemoryStream(png, writable: false))
        {
            await files.SaveAsync(key, stream, "image/png", ct);
        }
        return new StoredFile(key, name, "image/png", png.LongLength, Convert.ToHexString(SHA256.HashData(png)));
    }

    private async Task SeedPaymentsAsync(Lease lease, AppUser tenantUser, AppUser landlord, int unpaidMonths,
        decimal? partialOnFirstUnpaid, bool submitForLatest, CancellationToken ct)
    {
        var today = clock.Today();
        var charges = await db.RentCharges.Where(c => c.LeaseId == lease.Id).OrderBy(c => c.Period).ToListAsync(ct);
        var paidInFull = charges.Count - unpaidMonths;

        for (var i = 0; i < charges.Count; i++)
        {
            var charge = charges[i];
            decimal? amount = i < paidInFull ? charge.AmountDue
                : i == paidInFull && partialOnFirstUnpaid is { } partial ? partial
                : i == charges.Count - 1 && submitForLatest ? charge.AmountDue
                : null;
            if (amount is null) continue;

            var payment = new ProofOfPayment(lease.OwnerId, lease.Id, lease.TenantId, amount.Value, charge.DueDate,
                charge.BillingPeriod, $"RENT {charge.Period}",
                await StoreDemoProofAsync(lease, charge.Period, amount.Value, ct), tenantUser.Id, today);
            db.Payments.Add(payment);

            var leaveForReview = i == charges.Count - 1 && submitForLatest;
            if (!leaveForReview)
            {
                payment.Verify(landlord.Id, landlord.DisplayName, clock.UtcNow.AddSeconds(i));
                await receipts.IssueAsync(payment, ct);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>A tiny synthetic "bank confirmation" PDF, different per payment so the duplicate check passes.</summary>
    private async Task<StoredFile> StoreDemoProofAsync(Lease lease, string period, decimal amount, CancellationToken ct)
    {
        var text = $"Synthetic EFT confirmation - {lease.LeaseNumber} - {period} - R {amount.ToString("0.00", CultureInfo.InvariantCulture)}";
        var content = $"BT /F1 11 Tf 20 60 Td ({text}) Tj ET";
        var pdf = Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
            "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 500 120]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n" +
            $"4 0 obj<</Length {content.Length}>>stream\n{content}\nendstream endobj\n" +
            "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n");
        var key = $"payments/{lease.Id:N}/{Guid.NewGuid():N}.pdf";
        using (var stream = new MemoryStream(pdf, writable: false))
        {
            await files.SaveAsync(key, stream, "application/pdf", ct);
        }
        return new StoredFile(key, $"eft-confirmation-{period}.pdf", "application/pdf", pdf.LongLength,
            Convert.ToHexString(SHA256.HashData(pdf)));
    }

    private async Task<Lease> SeedSignedLeaseAsync(Unit unit, Tenant tenant, AppUser tenantUser, AppUser landlord,
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
        return lease;
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
