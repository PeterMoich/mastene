using TenantManager.Domain.Common;

namespace TenantManager.Domain.Messaging;

public enum ConsentSource
{
    /// <summary>The tenant ticked the box in the app themselves.</summary>
    Tenant,

    /// <summary>The landlord recorded consent the tenant gave another way (e.g. a signed form), with a note.</summary>
    Landlord,

    /// <summary>The tenant replied STOP on WhatsApp.</summary>
    WhatsAppStop,
}

/// <summary>
/// The wording a tenant agrees to. Stored by version with every consent record, so it is always
/// known what someone agreed to. LEGAL REVIEW NEEDED: POPIA requires consent to be voluntary,
/// specific and informed; this wording is a placeholder.
/// </summary>
public static class ConsentText
{
    public const string CurrentVersion = "wa-consent-v1";

    public const string Current =
        "I agree to receive WhatsApp messages from my landlord about my lease, rent, payments and maintenance " +
        "issues, on the phone number in my tenant details. I can withdraw at any time in the app or by replying STOP.";
}

/// <summary>
/// BR16: one row per grant or withdrawal, never changed. The latest row for a tenant decides whether
/// they may be messaged on WhatsApp.
/// </summary>
public sealed class ConsentRecord : Entity, IOwned, IAuditable
{
    public const int NoteMax = 300;

    private ConsentRecord() { } // EF Core

    private ConsentRecord(Guid ownerId, Guid tenantId, bool granted, string phone, ConsentSource source,
        string recordedByName, Guid? recordedBy, string? note, DateTimeOffset at)
    {
        OwnerId = ownerId;
        TenantId = tenantId;
        Granted = granted;
        Phone = phone;
        Source = source;
        TextVersion = ConsentText.CurrentVersion;
        RecordedBy = recordedBy;
        RecordedByName = recordedByName;
        Note = Guard.Optional(note, "note", NoteMax);
        At = at;
    }

    /// <summary>
    /// The landlord may only record consent with a note saying how it was given (e.g. "signed form on
    /// 3 Nov"). Withdrawal never needs a reason.
    /// </summary>
    public static ConsentRecord Grant(Guid ownerId, Guid tenantId, string phone, ConsentSource source,
        string byName, Guid? byUserId, string? note, DateTimeOffset at)
    {
        if (source == ConsentSource.WhatsAppStop) throw new ArgumentException("STOP can only withdraw consent.");
        if (source == ConsentSource.Landlord && string.IsNullOrWhiteSpace(note))
            throw new DomainRuleException("note.required",
                "Say how the tenant gave consent (for example, a signed form and its date).");
        return new ConsentRecord(ownerId, tenantId, true, phone, source, byName, byUserId, note, at);
    }

    public static ConsentRecord Withdraw(Guid ownerId, Guid tenantId, string phone, ConsentSource source,
        string byName, Guid? byUserId, string? note, DateTimeOffset at) =>
        new(ownerId, tenantId, false, phone, source, byName, byUserId, note, at);

    public Guid OwnerId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Channel { get; private set; } = "WhatsApp";
    public bool Granted { get; private set; }

    /// <summary>The number consent was given for. A changed phone number needs fresh consent.</summary>
    public string Phone { get; private set; } = string.Empty;

    public ConsentSource Source { get; private set; }
    public string TextVersion { get; private set; } = string.Empty;
    public Guid? RecordedBy { get; private set; }
    public string RecordedByName { get; private set; } = string.Empty;
    public string? Note { get; private set; }
    public DateTimeOffset At { get; private set; }

    /// <summary>POPIA erasure: keeps the record that consent was given or withdrawn, without the person's details.</summary>
    public void Redact()
    {
        Phone = "redacted";
        if (Source != ConsentSource.Landlord) RecordedByName = Privacy.FormerTenant;
    }

    /// <summary>BR16: messages may be sent only when the latest record is a grant for the tenant's current number.</summary>
    public static bool Allows(ConsentRecord? latest, string currentPhone) =>
        latest is { Granted: true } && latest.Phone == currentPhone;
}
