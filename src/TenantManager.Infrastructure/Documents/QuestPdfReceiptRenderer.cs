using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TenantManager.Application.Payments;
using TenantManager.Domain.Payments;

namespace TenantManager.Infrastructure.Documents;

/// <summary>The receipt issued when a landlord verifies a payment (ADR-0004).</summary>
public sealed class QuestPdfReceiptRenderer : IReceiptRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public byte[] Render(ReceiptPdfModel m) => Document.Create(container => container.Page(page =>
    {
        page.Size(PageSizes.A5);
        page.Margin(1.5f, QuestPDF.Infrastructure.Unit.Centimetre);
        page.DefaultTextStyle(t => t.FontSize(9.5f).LineHeight(1.3f));

        page.Header().Column(header =>
        {
            header.Item().Background(Colors.Amber.Lighten4).Padding(4)
                .Text("SAMPLE TEST DOCUMENT - NOT A TAX INVOICE").FontSize(8).Bold().FontColor(Colors.Amber.Darken4);
            header.Item().PaddingTop(8).Row(row =>
            {
                row.RelativeItem().Text("Rent receipt").FontSize(16).Bold();
                row.ConstantItem(120).AlignRight().Text(m.ReceiptNumber).FontSize(12).SemiBold();
            });
            header.Item().Text($"Issued {m.IssuedAt.UtcDateTime.ToString("d MMMM yyyy HH:mm 'UTC'", Culture)}")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });

        page.Content().PaddingVertical(10).Column(content =>
        {
            content.Spacing(8);
            content.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(95);
                    c.RelativeColumn();
                });

                void Row(string label, string value)
                {
                    table.Cell().PaddingVertical(1).Text(label).SemiBold();
                    table.Cell().PaddingVertical(1).Text(value);
                }

                Row("Received from", m.TenantName);
                Row("Received by", m.LandlordName);
                Row("Premises", $"{m.UnitLabel}, {m.PropertyName}");
                Row("Address", m.PropertyAddress);
                Row("Lease", $"{m.LeaseNumber} (version {m.LeaseVersion})");
                Row("Amount received", Money(m.Amount));
                Row("Paid on", m.PaidOn.ToString("d MMMM yyyy", Culture));
                if (!string.IsNullOrWhiteSpace(m.Reference)) Row("Reference", m.Reference);
            });

            content.Item().PaddingTop(4).Text("Applied to").Bold();
            content.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn();
                    c.ConstantColumn(90);
                });
                table.Cell().BorderBottom(0.5f).Text("Rent for").SemiBold();
                table.Cell().BorderBottom(0.5f).AlignRight().Text("Amount").SemiBold();
                foreach (var line in m.Lines)
                {
                    table.Cell().PaddingVertical(1).Text(MonthName(line.Period));
                    table.Cell().PaddingVertical(1).AlignRight().Text(Money(line.Amount));
                }
                if (m.UnallocatedCredit > 0)
                {
                    table.Cell().PaddingVertical(1).Text("Kept as credit for future rent");
                    table.Cell().PaddingVertical(1).AlignRight().Text(Money(m.UnallocatedCredit));
                }
            });

            var balance = m.BalanceAfter > 0
                ? $"Balance still owing on this lease: {Money(m.BalanceAfter)}"
                : m.BalanceAfter < 0
                    ? $"This lease is in credit: {Money(-m.BalanceAfter)}"
                    : "This lease is fully paid up.";
            content.Item().PaddingTop(6).Text(balance).SemiBold();
            content.Item().Text($"Payment checked and accepted by {m.VerifiedBy}.").FontColor(Colors.Grey.Darken1);
        });

        page.Footer().AlignCenter().Text(text =>
        {
            text.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(Colors.Grey.Darken1));
            text.Span($"{m.ReceiptNumber} · Mastene");
        });
    })).GeneratePdf();

    internal static string Money(decimal amount) => "R " + amount.ToString("#,##0.00", Culture);

    private static string MonthName(string period) =>
        BillingPeriod.Parse(period).FirstDay.ToString("MMMM yyyy", Culture);
}
