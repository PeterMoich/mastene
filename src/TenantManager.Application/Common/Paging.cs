using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace TenantManager.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>Common list parameters. Sort is a field name, prefixed with '-' for descending (e.g. "-createdAt").</summary>
public abstract record PagedQuery
{
    public const int MaxPageSize = 100;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string? Sort { get; init; }
}

public sealed class NotFoundException(string entity, object id)
    : Exception($"{entity} '{id}' was not found.");

public static class QueryableExtensions
{
    /// <summary>
    /// Applies a whitelisted sort. Unknown sort keys fall back to the default rather than erroring,
    /// and only keys in <paramref name="sorts"/> can ever reach the database.
    /// </summary>
    public static IQueryable<T> ApplySort<T>(this IQueryable<T> query, string? sort,
        IReadOnlyDictionary<string, Expression<Func<T, object>>> sorts, string defaultSort)
    {
        var key = string.IsNullOrWhiteSpace(sort) ? defaultSort : sort.Trim();
        var descending = key.StartsWith('-');
        var field = descending ? key[1..] : key;

        if (!sorts.TryGetValue(field, out var selector))
        {
            descending = defaultSort.StartsWith('-');
            selector = sorts[descending ? defaultSort[1..] : defaultSort];
        }

        return descending ? query.OrderByDescending(selector) : query.OrderBy(selector);
    }

    public static async Task<PagedResult<TOut>> ToPagedAsync<T, TOut>(this IQueryable<T> query, PagedQuery paging,
        Expression<Func<T, TOut>> projection, CancellationToken ct)
    {
        var page = Math.Max(1, paging.Page);
        var size = Math.Clamp(paging.PageSize, 1, PagedQuery.MaxPageSize);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * size).Take(size).Select(projection).ToListAsync(ct);
        return new PagedResult<TOut>(items, page, size, total);
    }
}
