using System.Net.Mail;
using System.Text.RegularExpressions;
using TenantManager.Domain.Common;

namespace TenantManager.Domain.Tenants;

/// <summary>
/// What a tenant still owes or holds. Supplied by the lease and payment modules
/// (Phase 2 and 3); until then it is always "nothing".
/// </summary>
public readonly record struct TenantObligations(bool HasActiveLease, decimal OutstandingBalance)
{
    public static TenantObligations None => new(false, 0m);
}

/// <summary>
/// A person who rents (or has rented) a unit. Holds only the contact details the app needs.
/// No ID number is stored in Phase 1: it isn't needed yet, and POPIA's minimality principle
/// says not to collect it until a feature requires it (flagged for legal review).
/// </summary>
public sealed partial class Tenant : Entity, IOwned, ISoftDeletable, IAuditable
{
    public const int NameMax = 80;
    public const int EmailMax = 256;

    private Tenant() { } // EF Core

    public Tenant(Guid ownerId, string firstName, string lastName, string email, string phone)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerId));
        OwnerId = ownerId;
        Update(firstName, lastName, email, phone);
    }

    public Guid OwnerId { get; private set; }
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;

    /// <summary>Stored lower-case so uniqueness checks are case-insensitive.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>E.164 format, e.g. +27821234567. WhatsApp (Phase 5) needs this form.</summary>
    public string Phone { get; private set; } = string.Empty;

    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }

    public string FullName => $"{FirstName} {LastName}";

    public void Update(string firstName, string lastName, string email, string phone)
    {
        FirstName = Guard.NotBlank(firstName, "firstName", NameMax);
        LastName = Guard.NotBlank(lastName, "lastName", NameMax);
        Email = NormaliseEmail(email);
        Phone = NormalisePhone(phone);
    }

    /// <summary>
    /// BR19: a tenant cannot be deleted while they have an active lease or owe money.
    /// The record is soft-deleted so history (leases, payments, audit) stays intact.
    /// </summary>
    public void Delete(TenantObligations obligations, Guid deletedBy, DateTimeOffset at)
    {
        if (IsDeleted) return;
        if (obligations.HasActiveLease)
            throw new DomainRuleException("tenant.has_active_lease",
                "This tenant has an active lease and cannot be deleted.", RuleKind.Conflict);
        if (obligations.OutstandingBalance > 0)
            throw new DomainRuleException("tenant.has_outstanding_balance",
                $"This tenant owes R{obligations.OutstandingBalance:N2} and cannot be deleted.", RuleKind.Conflict);

        IsDeleted = true;
        DeletedAt = at;
        DeletedBy = deletedBy;
    }

    public static string NormaliseEmail(string? value)
    {
        var trimmed = Guard.NotBlank(value, "email", EmailMax).ToLowerInvariant();
        if (!MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed || !trimmed[trimmed.IndexOf('@')..].Contains('.'))
            throw new DomainRuleException("email.invalid", "Email address is not valid.");
        return trimmed;
    }

    /// <summary>
    /// Accepts +27821234567, 27821234567 or the local form 0821234567 (assumed South African)
    /// and returns E.164. Spaces and dashes are ignored.
    /// </summary>
    public static string NormalisePhone(string? value)
    {
        var digits = SeparatorChars().Replace(value?.Trim() ?? string.Empty, string.Empty);
        if (digits.StartsWith('0') && digits.Length == 10) digits = "+27" + digits[1..];
        else if (digits.StartsWith("27") && digits.Length == 11) digits = "+" + digits;

        if (!E164().IsMatch(digits))
            throw new DomainRuleException("phone.invalid",
                "Phone must be a valid number, e.g. 082 123 4567 or +27 82 123 4567.");
        return digits;
    }

    [GeneratedRegex(@"[\s\-()]")]
    private static partial Regex SeparatorChars();

    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex E164();
}
