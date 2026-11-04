using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TenantManager.Application.Leases;

namespace TenantManager.Infrastructure.Documents;

/// <summary>
/// Lays out the lease PDF with QuestPDF (Community licence, ADR-0004). The wording comes from
/// <see cref="LeaseTemplate"/>, which is a placeholder marked LEGAL REVIEW NEEDED.
/// </summary>
public sealed class QuestPdfLeaseRenderer : ILeaseDocumentRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public byte[] Render(LeasePdfModel model)
    {
        var clauses = LeaseTemplate.For(model.TemplateVersion).Clauses(model);

        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(10).LineHeight(1.3f));

            page.Header().Column(header =>
            {
                header.Item().Background(Colors.Amber.Lighten4).Padding(6)
                    .Text("SAMPLE TEST DOCUMENT - LEGAL REVIEW NEEDED - NOT FOR REAL USE")
                    .FontSize(9).Bold().FontColor(Colors.Amber.Darken4);
                header.Item().PaddingTop(10).Text(model.IsRenewal ? "Residential Lease Renewal" : "Residential Lease Agreement")
                    .FontSize(18).Bold();
                header.Item().Text($"Lease {model.LeaseNumber}, version {model.Version} · Template {model.TemplateVersion}")
                    .FontSize(9).FontColor(Colors.Grey.Darken1);
            });

            page.Content().PaddingVertical(12).Column(content =>
            {
                content.Spacing(8);
                content.Item().Element(c => Summary(c, model));
                for (var i = 0; i < clauses.Count; i++)
                {
                    var (title, body) = clauses[i];
                    var number = i + 1;
                    content.Item().Text(text =>
                    {
                        text.Span($"{number}. {title}. ").Bold();
                        text.Span(body);
                    });
                }
                content.Item().PaddingTop(10).Element(c => Signatures(c, model));
            });

            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                text.Span($"{model.LeaseNumber} v{model.Version} · Generated {model.GeneratedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", Culture)} · Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        })).GeneratePdf();
    }

    private static void Summary(IContainer container, LeasePdfModel m)
    {
        container.Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(8).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(130);
                columns.RelativeColumn();
            });

            void Row(string label, string value)
            {
                table.Cell().PaddingVertical(2).Text(label).SemiBold();
                table.Cell().PaddingVertical(2).Text(value);
            }

            Row("Landlord", m.LandlordName);
            Row("Tenant", $"{m.TenantName} ({m.TenantEmail}, {m.TenantPhone})");
            Row("Premises", $"{m.UnitLabel}, {m.PropertyName}, {m.PropertyAddress}");
            Row("Term", $"{Date(m.StartDate)} to {Date(m.EndDate)} (12 months)");
            Row("Monthly rent", Money(m.MonthlyRent));
            Row("Deposit", Money(m.DepositAmount));
            Row("Escalation on renewal", $"{m.EscalationPct.ToString("0.##", Culture)}%");
        });
    }

    private static void Signatures(IContainer container, LeasePdfModel m)
    {
        container.Column(column =>
        {
            column.Spacing(4);
            column.Item().Text("Signatures").FontSize(12).Bold();
            if (m.Signatures.Count == 0)
            {
                column.Item().Text("Awaiting signature by the landlord and the tenant in Tenant Manager.")
                    .FontColor(Colors.Grey.Darken1);
                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(70);
                    c.RelativeColumn(2);
                    c.RelativeColumn(2);
                    c.RelativeColumn(1);
                });
                foreach (var header in new[] { "Party", "Signed by", "Signed at (UTC)", "IP address" })
                    table.Cell().BorderBottom(0.5f).PaddingBottom(2).Text(header).SemiBold();
                foreach (var s in m.Signatures)
                {
                    table.Cell().PaddingVertical(2).Text(s.Party);
                    table.Cell().PaddingVertical(2).Text(s.Name);
                    table.Cell().PaddingVertical(2).Text(s.SignedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", Culture));
                    table.Cell().PaddingVertical(2).Text(s.IpAddress ?? "-");
                }
            });
            column.Item().Text("Each party confirmed the lease in the app while signed in. This is a record of " +
                               "in-app confirmation, not an advanced electronic signature (LEGAL REVIEW NEEDED).")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    internal static string Money(decimal amount) => "R " + amount.ToString("#,##0.00", Culture);

    internal static string Date(DateOnly date) => date.ToString("d MMMM yyyy", Culture);
}

/// <summary>
/// The lease wording. Placeholder text written for this test project, marked LEGAL REVIEW NEEDED:
/// it has not been checked against the Rental Housing Act, the Consumer Protection Act or POPIA.
/// Changing the wording means adding a new version, so existing PDFs stay reproducible.
/// </summary>
public sealed class LeaseTemplate
{
    private static readonly LeaseTemplate V0Test = new();

    public static LeaseTemplate For(string version) => version switch
    {
        "v0-test" => V0Test,
        _ => V0Test, // unknown versions fall back rather than block lease generation in a test project
    };

    public IReadOnlyList<(string Title, string Body)> Clauses(LeasePdfModel m) =>
    [
        ("Parties", $"This lease is between {m.LandlordName} (the Landlord) and {m.TenantName} (the Tenant)."),
        ("Premises", $"The Landlord lets {m.UnitLabel} at {m.PropertyName}, {m.PropertyAddress} (the Premises) to the Tenant."),
        ("Term", $"The lease runs for 12 months from {QuestPdfLeaseRenderer.Date(m.StartDate)} to " +
                 $"{QuestPdfLeaseRenderer.Date(m.EndDate)}, both days included."),
        ("Rent", $"The Tenant pays {QuestPdfLeaseRenderer.Money(m.MonthlyRent)} per month in advance, on or before " +
                 "the due day set by the Landlord, and uploads proof of payment in Tenant Manager."),
        ("Deposit", $"The Tenant pays a deposit of {QuestPdfLeaseRenderer.Money(m.DepositAmount)}, which the Landlord " +
                    "holds and refunds as required by applicable law. [LEGAL REVIEW NEEDED]"),
        ("Inspections", "The Landlord and the Tenant inspect the Premises together at move-in and move-out and " +
                        "record the condition with photos. [LEGAL REVIEW NEEDED]"),
        ("Renewal", $"Both parties may agree to renew for a further 12 months. On renewal the rent increases by " +
                    $"{m.EscalationPct.ToString("0.##", CultureInfo.InvariantCulture)}%. If neither party acts, the " +
                    "lease continues as set out in the Landlord's settings. [LEGAL REVIEW NEEDED]"),
        ("Termination", "Either party may end the lease early by giving the notice required by applicable law. " +
                        "[LEGAL REVIEW NEEDED]"),
        ("Maintenance", "The Tenant reports maintenance issues in Tenant Manager. The Landlord responds within the " +
                        "target times shown there."),
        ("Personal information", "The Landlord processes the Tenant's personal information only to manage this " +
                                 "lease, and the Tenant may ask for a copy of it. [LEGAL REVIEW NEEDED]"),
    ];
}
