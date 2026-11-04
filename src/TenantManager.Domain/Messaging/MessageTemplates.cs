using System.Text.RegularExpressions;
using TenantManager.Domain.Common;

namespace TenantManager.Domain.Messaging;

/// <summary>A WhatsApp message template. Parameters are {{1}}, {{2}}, ... as in Meta's templates.</summary>
/// <param name="Manual">The landlord may send it by hand from the conversation screen.</param>
public sealed partial record MessageTemplate(string Name, string Description, string Body, IReadOnlyList<string> ParameterNames,
    bool Manual = false)
{
    public const int ParameterMax = 200;

    public RenderedTemplate Render(IReadOnlyList<string> values)
    {
        if (values.Count != ParameterNames.Count)
            throw new DomainRuleException("template.parameters",
                $"The {Name} template needs {ParameterNames.Count} value(s): {string.Join(", ", ParameterNames)}.");
        var clean = new List<string>();
        for (var i = 0; i < values.Count; i++)
        {
            // WhatsApp refuses parameters with new lines, tabs or more than four spaces in a row.
            var value = Whitespace().Replace(values[i] ?? string.Empty, " ").Trim();
            if (value.Length == 0)
                throw new DomainRuleException("template.parameters", $"Give a value for {ParameterNames[i]}.");
            if (value.Length > ParameterMax)
                throw new DomainRuleException("template.parameters", $"{ParameterNames[i]} is too long.");
            clean.Add(value);
        }
        var body = Placeholder().Replace(Body, m => clean[int.Parse(m.Groups[1].Value) - 1]);
        return new RenderedTemplate(Name, body, clean);
    }

    [GeneratedRegex(@"\{\{(\d+)\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed record RenderedTemplate(string Name, string Body, IReadOnlyList<string> Parameters);

/// <summary>
/// The templates the app uses. In production each must be created and approved in Meta's WhatsApp
/// Manager with the same name, language and wording, as a Utility template. The wording is ours, not
/// legal text.
/// </summary>
public static class MessageTemplates
{
    public const string Language = "en";

    public static readonly MessageTemplate RentDue = new("rent_due_reminder",
        "Rent is due in a few days",
        "Hi {{1}}, a friendly reminder that your rent of {{2}} for {{3}} is due on {{4}}.",
        ["tenant first name", "amount", "month", "due date"]);

    public static readonly MessageTemplate RentOverdue = new("rent_overdue",
        "Rent is overdue",
        "Hi {{1}}, your rent for {{2}} is overdue and {{3}} is still outstanding. Please pay, or upload your proof of payment in the Tenant Manager app.",
        ["tenant first name", "month", "amount outstanding"]);

    public static readonly MessageTemplate PaymentReceived = new("payment_received",
        "A payment was verified",
        "Hi {{1}}, thank you. Your payment of {{2}} has been verified. Receipt {{3}} is in the Tenant Manager app.",
        ["tenant first name", "amount", "receipt number"]);

    public static readonly MessageTemplate PaymentRejected = new("payment_rejected",
        "A proof of payment was rejected",
        "Hi {{1}}, your proof of payment of {{2}} could not be accepted: {{3}}. Please upload it again in the Tenant Manager app.",
        ["tenant first name", "amount", "reason"]);

    public static readonly MessageTemplate IssueUpdate = new("issue_update",
        "An issue moved on",
        "Hi {{1}}, an update on your maintenance issue \"{{2}}\": {{3}}",
        ["tenant first name", "issue title", "update"]);

    public static readonly MessageTemplate LeaseExpiry = new("lease_expiry_notice",
        "The lease is ending",
        "Hi {{1}}, your lease for {{2}} ends on {{3}}. Please let us know if you would like to renew.",
        ["tenant first name", "home", "end date"]);

    public static readonly MessageTemplate RenewalReady = new("renewal_ready",
        "A renewal is waiting to be signed",
        "Hi {{1}}, the renewal of your lease for {{2}}, starting {{3}}, is ready for you to sign in the Tenant Manager app.",
        ["tenant first name", "home", "start date"]);

    public static readonly MessageTemplate MessageWaiting = new("message_waiting",
        "Ask the tenant to get in touch (use this outside the 24-hour window)",
        "Hi {{1}}, {{2}} would like to talk to you about {{3}}. Reply to this message to continue.",
        ["tenant first name", "landlord name", "topic"], Manual: true);

    public static readonly IReadOnlyList<MessageTemplate> All =
        [RentDue, RentOverdue, PaymentReceived, PaymentRejected, IssueUpdate, LeaseExpiry, RenewalReady, MessageWaiting];

    public static MessageTemplate Find(string name) =>
        All.FirstOrDefault(t => t.Name == name)
        ?? throw new DomainRuleException("template.unknown", $"There is no template called {name}.");
}
