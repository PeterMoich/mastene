using System.Security.Cryptography;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Application.Common;
using TenantManager.Domain.Common;
using TenantManager.Infrastructure.Persistence;

namespace TenantManager.Infrastructure.Identity;

public sealed class UserAccountService(
    UserManager<AppUser> users,
    AppDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IValidator<CreateUserRequest> createValidator) : IUserAccountService
{
    public async Task<PagedResult<UserDto>> ListAsync(UserListQuery query, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var q =
            from u in db.Users.AsNoTracking()
            let role = (from ur in db.UserRoles
                        join r in db.Roles on ur.RoleId equals r.Id
                        where ur.UserId == u.Id
                        select r.Name).FirstOrDefault()
            select new { User = u, Role = role };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(x => x.User.Email!.Contains(term) || x.User.DisplayName.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(query.Role)) q = q.Where(x => x.Role == query.Role);

        q = query.Sort?.Trim() switch
        {
            "-createdAt" => q.OrderByDescending(x => x.User.CreatedAt),
            "createdAt" => q.OrderBy(x => x.User.CreatedAt),
            "-email" => q.OrderByDescending(x => x.User.Email),
            _ => q.OrderBy(x => x.User.Email),
        };

        return await q.ToPagedAsync(query, x => new UserDto(
            x.User.Id, x.User.Email!, x.User.DisplayName, x.Role ?? string.Empty, x.User.TenantId,
            x.User.LockoutEnd != null && x.User.LockoutEnd > now, x.User.CreatedAt), ct);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        await createValidator.ValidateAndThrowAsync(request, ct);
        var user = new AppUser
        {
            UserName = request.Email.Trim().ToLowerInvariant(),
            Email = request.Email.Trim().ToLowerInvariant(),
            EmailConfirmed = true,
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = clock.UtcNow,
        };
        await CreateWithRoleAsync(user, request.Password, request.Role);
        return ToDto(user, request.Role);
    }

    public async Task<UserDto> SetRoleAsync(Guid userId, string role, CancellationToken ct)
    {
        var user = await FindStaffForChangeAsync(userId);
        var current = await users.GetRolesAsync(user);
        Check(await users.RemoveFromRolesAsync(user, current));
        Check(await users.AddToRoleAsync(user, role));
        await RevokeSessionsAsync(user.Id, ct); // new role takes effect at next sign-in
        return ToDto(user, role);
    }

    public async Task<UserDto> SetLockedAsync(Guid userId, bool locked, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);
        if (user.Id == currentUser.UserId)
            throw new DomainRuleException("user.self_change", "You cannot lock your own account.", RuleKind.Conflict);

        await SetLockAsync(user, locked, ct);
        var role = (await users.GetRolesAsync(user)).FirstOrDefault() ?? string.Empty;
        return ToDto(user, role);
    }

    public async Task<TenantLoginCreated> CreateTenantLoginAsync(Guid tenantId, Guid ownerId, string email,
        string displayName, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.TenantId == tenantId, ct))
            throw new DomainRuleException("tenant.login_exists", "This tenant already has a login.", RuleKind.Conflict);

        var password = GenerateTemporaryPassword();
        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            TenantId = tenantId,
            OwnerId = ownerId,
            CreatedAt = clock.UtcNow,
        };
        await CreateWithRoleAsync(user, password, Roles.Tenant);
        return new TenantLoginCreated(user.Id, email, password);
    }

    public async Task DisableTenantLoginAsync(Guid tenantId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.TenantId == tenantId, ct);
        if (user is not null) await SetLockAsync(user, true, ct);
    }

    public async Task<IReadOnlySet<Guid>> TenantIdsWithLoginAsync(IReadOnlyCollection<Guid> tenantIds,
        CancellationToken ct)
    {
        if (tenantIds.Count == 0) return new HashSet<Guid>();
        var ids = await db.Users.Where(u => u.TenantId != null && tenantIds.Contains(u.TenantId.Value))
            .Select(u => u.TenantId!.Value).ToListAsync(ct);
        return ids.ToHashSet();
    }

    private async Task CreateWithRoleAsync(AppUser user, string password, string role)
    {
        if (await users.FindByEmailAsync(user.Email!) is not null)
            throw new DomainRuleException("user.email_taken", "A user with this email already exists.",
                RuleKind.Conflict);

        Check(await users.CreateAsync(user, password));
        Check(await users.AddToRoleAsync(user, role));
    }

    private async Task<AppUser> FindStaffForChangeAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);
        if (user.Id == currentUser.UserId)
            throw new DomainRuleException("user.self_change", "You cannot change your own role.", RuleKind.Conflict);
        if (user.TenantId is not null)
            throw new DomainRuleException("user.is_tenant", "A tenant login cannot be given a staff role.",
                RuleKind.Conflict);
        return user;
    }

    private async Task SetLockAsync(AppUser user, bool locked, CancellationToken ct)
    {
        Check(await users.SetLockoutEnabledAsync(user, true));
        Check(await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null));
        if (locked) await RevokeSessionsAsync(user.Id, ct);
    }

    private Task RevokeSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private UserDto ToDto(AppUser u, string role) => new(
        u.Id, u.Email ?? string.Empty, u.DisplayName, role, u.TenantId,
        u.LockoutEnd is { } end && end > clock.UtcNow, u.CreatedAt);

    private static void Check(IdentityResult result)
    {
        if (result.Succeeded) return;
        // Identity's password and username errors are user-facing, so surface them as validation errors.
        throw new ValidationException(result.Errors.Select(e =>
            new FluentValidation.Results.ValidationFailure(e.Code.StartsWith("Password") ? "password" : "user",
                e.Description)));
    }

    /// <summary>16 random characters that always satisfy the password policy.</summary>
    internal static string GenerateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*?";
        const string all = upper + lower + digits + symbols;

        var chars = new char[16];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < chars.Length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }
}
