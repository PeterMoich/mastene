using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Common;
using TenantManager.Domain.Common;
using TenantManager.Domain.Issues;
using TenantManager.Domain.Tenants;

namespace TenantManager.Application.Issues;

public sealed record ContractorRequest(string Name, string Trade, string Phone, string? Email, string? Notes);

/// <param name="UnfinishedIssues">Issues assigned to them that aren't resolved yet.</param>
public sealed record ContractorDto(Guid Id, string Name, string Trade, string Phone, string? Email, string? Notes,
    int UnfinishedIssues);

public sealed record ContractorListQuery : PagedQuery
{
    /// <summary>Matches name, trade or phone.</summary>
    public string? Search { get; init; }
}

public sealed class ContractorRequestValidator : AbstractValidator<ContractorRequest>
{
    public ContractorRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Contractor.NameMax);
        RuleFor(x => x.Trade).NotEmpty().MaximumLength(Contractor.TradeMax);
        RuleFor(x => x.Phone).NotEmpty().Must(BeAValidPhone)
            .WithMessage("Phone must be a valid number, e.g. 082 123 4567 or +27 82 123 4567.");
        RuleFor(x => x.Email).MaximumLength(Contractor.EmailMax).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Notes).MaximumLength(Contractor.NotesMax);
    }

    private static bool BeAValidPhone(string? phone)
    {
        try { Tenant.NormalisePhone(phone); return true; }
        catch (DomainRuleException) { return false; }
    }
}

/// <summary>The landlord's contractors. Soft-deleted, so issues they worked on still show their name.</summary>
public sealed class ContractorService(
    IApplicationDbContext db, ICurrentUser user, IClock clock, IValidator<ContractorRequest> validator)
{
    private static readonly IssueStatus[] Unfinished = [IssueStatus.Open, IssueStatus.Acknowledged, IssueStatus.InProgress];

    private static readonly Dictionary<string, Expression<Func<Contractor, object>>> Sorts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = c => c.Name,
            ["trade"] = c => c.Trade,
            ["createdAt"] = c => c.CreatedAt,
        };

    private IQueryable<Contractor> Owned()
    {
        var ownerId = user.RequireOwnerId();
        return db.Contractors.Where(c => c.OwnerId == ownerId);
    }

    public Task<PagedResult<ContractorDto>> ListAsync(ContractorListQuery query, CancellationToken ct)
    {
        var q = Owned().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(c => c.Name.Contains(term) || c.Trade.Contains(term) || c.Phone.Contains(term));
        }
        return q.ApplySort(query.Sort, Sorts, "name").ToPagedAsync(query, c => new ContractorDto(c.Id, c.Name, c.Trade,
            c.Phone, c.Email, c.Notes,
            db.Issues.Count(i => i.ContractorId == c.Id && Unfinished.Contains(i.Status))), ct);
    }

    public async Task<ContractorDto> GetAsync(Guid id, CancellationToken ct) =>
        await Owned().AsNoTracking().Where(c => c.Id == id)
            .Select(c => new ContractorDto(c.Id, c.Name, c.Trade, c.Phone, c.Email, c.Notes,
                db.Issues.Count(i => i.ContractorId == c.Id && Unfinished.Contains(i.Status))))
            .FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Contractor", id);

    public async Task<ContractorDto> CreateAsync(ContractorRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var contractor = new Contractor(user.RequireOwnerId(), request.Name, request.Trade, request.Phone, request.Email,
            request.Notes);
        db.Contractors.Add(contractor);
        await db.SaveChangesAsync(ct);
        return await GetAsync(contractor.Id, ct);
    }

    public async Task<ContractorDto> UpdateAsync(Guid id, ContractorRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var contractor = await Owned().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Contractor", id);
        contractor.Update(request.Name, request.Trade, request.Phone, request.Email, request.Notes);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var contractor = await Owned().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Contractor", id);
        var unfinished = await db.Issues.CountAsync(i => i.ContractorId == id && Unfinished.Contains(i.Status), ct);
        contractor.Delete(unfinished, user.RequireUserId(), clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }
}
