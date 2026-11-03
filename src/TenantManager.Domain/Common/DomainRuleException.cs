namespace TenantManager.Domain.Common;

public enum RuleKind
{
    /// <summary>The request breaks a business rule regardless of current state (HTTP 422).</summary>
    Violation,

    /// <summary>The request clashes with the current state of the data, e.g. deleting something in use (HTTP 409).</summary>
    Conflict,
}

/// <summary>
/// Thrown when a business rule is broken. <see cref="Code"/> is a stable, machine-readable
/// identifier (e.g. <c>tenant.has_active_lease</c>) that the API returns in ProblemDetails so the
/// frontend can show a specific message without parsing English text.
/// </summary>
public sealed class DomainRuleException(string code, string message, RuleKind kind = RuleKind.Violation)
    : Exception(message)
{
    public string Code { get; } = code;
    public RuleKind Kind { get; } = kind;
}

internal static class Guard
{
    public static string NotBlank(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new DomainRuleException($"{field}.required", $"{field} is required.");
        if (trimmed.Length > maxLength)
            throw new DomainRuleException($"{field}.too_long", $"{field} must be {maxLength} characters or fewer.");
        return trimmed;
    }

    public static string? Optional(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return NotBlank(trimmed, field, maxLength);
    }

    public static decimal Money(decimal value, string field, bool allowZero)
    {
        if (value < 0 || (!allowZero && value == 0))
            throw new DomainRuleException($"{field}.invalid",
                allowZero ? $"{field} cannot be negative." : $"{field} must be greater than zero.");
        if (decimal.Round(value, 2) != value)
            throw new DomainRuleException($"{field}.invalid", $"{field} can have at most two decimal places.");
        return value;
    }
}
