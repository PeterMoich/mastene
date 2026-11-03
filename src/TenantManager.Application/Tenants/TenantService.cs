using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Application.Common;
using TenantManager.Domain.Common;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Tenants;

public sealed record TenantRequest(string FirstName, string LastName, string Email, string Phone);

public sealed record TenantDto(
    Guid Id, string FirstName, string LastName, string Email, string Phone, bool HasLogin,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record TenantListQuery : PagedQuery
{
    /// <summary>Matches first name, last name, email or phone.</summary>
    public string? Search { get; init; }
}

public sealed class TenantRequestValidator : AbstractValidator<TenantRequest>
{
    public TenantRequestValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(Tenant.NameMax);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(Tenant.NameMax);
        RuleFor(x => x.Email).NotEmpty().MaximumLength(Tenant.EmailMax).EmailAddress();
        RuleFor(x => x.Phone).NotEmpty().Must(BeAValidPhone)
            .WithMessage("Phone must be a valid number, e.g. 082 123 4567 or +27 82 123 4567.");
    }

    private static bool BeAValidPhone(string? phone)
    {
        try { Tenant.NormalisePhone(phone); return true; }
        catch (DomainRuleException) { return false; }
    }
}

/// <summary>
/// Tenant records. Landlords see their own tenants; a user in the Tenant role sees only their
/// own record (BR01). Anything else returns "not found" so ids can't be probed.
/// </summary>
public sealed class TenantService(
    IApplicationDbContext db,
    ICurrentUser user,
    IClock clock,
    IValidator<TenantRequest> validator,
    ITenantObligationsProvider obligations,
    IUserAccountService accounts)
{
    private static readonly Dictionary<string, Expression<Func<Tenant, object>>> Sorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["lastName"] = t => t.LastName,
            ["firstName"] = t => t.FirstName,
            ["email"] = t => t.Email,
            ["createdAt"] = t => t.CreatedAt,
        };

    /// <summary>The tenants the current user may see.</summary>
    private IQueryable<Tenant> Visible()
    {
        if (user.IsTenant())
        {
            var ownId = user.TenantId ?? Guid.Empty;
            return db.Tenants.Where(t => t.Id == ownId);
        }
        var ownerId = user.RequireOwnerId();
        return db.Tenants.Where(t => t.OwnerId == ownerId);
    }

    public async Task<PagedResult<TenantDto>> ListAsync(TenantListQuery query, CancellationToken ct)
    {
        var q = Visible();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(t => t.FirstName.Contains(term) || t.LastName.Contains(term) ||
                             t.Email.Contains(term) || t.Phone.Contains(term));
        }

        var page = await q.ApplySort(query.Sort, Sorts, "lastName").ToPagedAsync(query, t => new TenantDto(
            t.Id, t.FirstName, t.LastName, t.Email, t.Phone, false, t.CreatedAt, t.UpdatedAt), ct);

        var withLogin = await accounts.TenantIdsWithLoginAsync(page.Items.Select(i => i.Id).ToList(), ct);
        return page with { Items = page.Items.Select(i => i with { HasLogin = withLogin.Contains(i.Id) }).ToList() };
    }

    public async Task<TenantDto> GetAsync(Guid id, CancellationToken ct)
    {
        var tenant = await Visible().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
                     ?? throw new NotFoundException("Tenant", id);
        return await ToDtoAsync(tenant, ct);
    }

    public async Task<TenantDto> CreateAsync(TenantRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var tenant = new Tenant(user.RequireOwnerId(), request.FirstName, request.LastName, request.Email, request.Phone);
        await EnsureEmailIsFreeAsync(tenant.Email, null, ct);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(tenant, ct);
    }

    public async Task<TenantDto> UpdateAsync(Guid id, TenantRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var tenant = await LoadOwnedAsync(id, ct);
        tenant.Update(request.FirstName, request.LastName, request.Email, request.Phone);
        await EnsureEmailIsFreeAsync(tenant.Email, tenant.Id, ct);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(tenant, ct);
    }

    /// <summary>BR19: soft delete, blocked while the tenant has an active lease or owes money.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var tenant = await LoadOwnedAsync(id, ct);
        var current = await obligations.GetAsync(tenant.Id, ct);
        tenant.Delete(current, user.RequireUserId(), clock.UtcNow);
        await db.SaveChangesAsync(ct);
        await accounts.DisableTenantLoginAsync(tenant.Id, ct);
    }

    /// <summary>
    /// Gives the tenant a login. Returns a one-time temporary password for the landlord to pass on.
    /// In production this would be an emailed invite link instead (no email sending in this project).
    /// </summary>
    public async Task<TenantLoginCreated> CreateLoginAsync(Guid id, CancellationToken ct)
    {
        var tenant = await LoadOwnedAsync(id, ct);
        return await accounts.CreateTenantLoginAsync(tenant.Id, tenant.OwnerId, tenant.Email, tenant.FullName, ct);
    }

    private async Task<Tenant> LoadOwnedAsync(Guid id, CancellationToken ct)
    {
        var ownerId = user.RequireOwnerId();
        return await db.Tenants.FirstOrDefaultAsync(t => t.Id == id && t.OwnerId == ownerId, ct)
               ?? throw new NotFoundException("Tenant", id);
    }

    // Friendly check; the filtered unique index (OwnerId, Email) is the real guarantee under concurrency.
    private async Task EnsureEmailIsFreeAsync(string email, Guid? exceptId, CancellationToken ct)
    {
        var ownerId = user.RequireOwnerId();
        var taken = await db.Tenants.AnyAsync(t => t.OwnerId == ownerId && t.Email == email && t.Id != exceptId, ct);
        if (taken)
            throw new DomainRuleException("tenant.email_taken", "Another tenant already uses this email address.",
                RuleKind.Conflict);
    }

    private async Task<TenantDto> ToDtoAsync(Tenant t, CancellationToken ct)
    {
        var withLogin = await accounts.TenantIdsWithLoginAsync([t.Id], ct);
        return new TenantDto(t.Id, t.FirstName, t.LastName, t.Email, t.Phone, withLogin.Contains(t.Id),
            t.CreatedAt, t.UpdatedAt);
    }
}
