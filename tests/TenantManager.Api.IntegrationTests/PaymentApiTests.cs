using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shouldly;
using TenantManager.Application.Common;
using TenantManager.Application.Leases;
using TenantManager.Application.Payments;
using TenantManager.Application.Settings;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;
using UglyToad.PdfPig;

namespace TenantManager.Api.IntegrationTests;

public static class PaymentHelpers
{
    /// <summary>A small, unique PDF-looking file (unique so the duplicate check doesn't trip unintentionally).</summary>
    public static byte[] UniquePdf(int size = 0)
    {
        var head = Encoding.ASCII.GetBytes($"%PDF-1.4\n% test proof {Guid.NewGuid()}\n");
        if (size <= head.Length) return head;
        var bytes = new byte[size];
        head.CopyTo(bytes, 0);
        return bytes;
    }

    public static async Task<HttpResponseMessage> UploadPaymentAsync(this HttpClient client, Guid leaseId, decimal amount,
        DateOnly paidOn, byte[] file, string fileName = "eft.pdf", string? period = null, string contentType = "application/pdf")
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(leaseId.ToString()), "LeaseId" },
            { new StringContent(amount.ToString(CultureInfo.InvariantCulture)), "Amount" },
            { new StringContent(paidOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "PaidOn" },
        };
        if (period is not null) form.Add(new StringContent(period), "Period");
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "File", fileName);
        return await client.PostAsync("/api/v1/payments", form);
    }

    public static async Task<PaymentDto> SubmitAsync(this HttpClient client, Guid leaseId, decimal amount, string? period = null)
    {
        var response = await client.UploadPaymentAsync(leaseId, amount, LeaseScenario.Today, UniquePdf(), period: period);
        await response.ShouldBeAsync(HttpStatusCode.Created);
        return await response.ReadAsync<PaymentDto>();
    }
}

[Collection(ApiCollection.Name)]
public class PaymentApiTests(ApiFactory api)
{
    private static DateOnly Today => LeaseScenario.Today;
    private static DateOnly FirstOfMonth => new(Today.Year, Today.Month, 1);

    /// <summary>An active lease that started on the 1st two months ago: three full monthly charges, two overdue.</summary>
    private async Task<(LeaseScenario S, LeaseDto Lease)> LeaseWithHistoryAsync()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        return (s, await s.ActiveAsync(FirstOfMonth.AddMonths(-2)));
    }

    private static async Task<LedgerDto> LedgerAsync(HttpClient client, Guid leaseId) =>
        await (await client.GetAsync($"/api/v1/leases/{leaseId}/ledger")).ReadAsync<LedgerDto>();

    [Fact]
    public async Task Starting_a_lease_raises_its_rent_charges_straight_away()
    {
        var (s, lease) = await LeaseWithHistoryAsync();

        var ledger = await LedgerAsync(s.Landlord, lease.Id);

        ledger.Lines.Count.ShouldBe(3);
        ledger.Lines.ShouldAllBe(l => l.AmountDue == 6500m && !l.Prorated);
        ledger.TotalDue.ShouldBe(19500m);
        ledger.Balance.ShouldBe(19500m);
        ledger.Lines[0].State.ShouldBe(ChargeState.Overdue);
    }

    [Fact]
    public async Task Tenant_submits_proof_and_the_landlord_sees_it_waiting_for_review()
    {
        var (s, lease) = await LeaseWithHistoryAsync();
        var tenant = await s.TenantClientAsync();

        var submitted = await tenant.SubmitAsync(lease.Id, 6500m);

        submitted.Status.ShouldBe(PaymentStatus.Submitted);
        submitted.FileName.ShouldBe("eft.pdf");
        submitted.AllowedActions.ShouldBe(["viewFile"]);
        var asLandlord = await (await s.Landlord.GetAsync($"/api/v1/payments/{submitted.Id}")).ReadAsync<PaymentDto>();
        asLandlord.AllowedActions.ShouldBe(["viewFile", "verify", "reject"]);

        var file = await s.Landlord.GetAsync($"/api/v1/payments/{submitted.Id}/file");
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        file.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");

        (await LedgerAsync(s.Landlord, lease.Id)).TotalPaid.ShouldBe(0m); // not counted until verified
    }

    [Fact]
    public async Task BR09_BR11_Verifying_applies_the_payment_oldest_first_and_issues_a_receipt()
    {
        var (s, lease) = await LeaseWithHistoryAsync();
        var tenant = await s.TenantClientAsync();
        var payment = await tenant.SubmitAsync(lease.Id, 3000m);

        var verified = await (await s.Landlord.PostAsync($"/api/v1/payments/{payment.Id}/verify", null)).ReadAsync<PaymentDto>();

        verified.Status.ShouldBe(PaymentStatus.Verified);
        verified.ReviewedByName.ShouldBe("Thandi Nkosi");
        verified.ReceiptNumber.ShouldStartWith($"R{Today.Year}-");
        var ledger = await LedgerAsync(s.Landlord, lease.Id);
        ledger.Lines[0].Paid.ShouldBe(3000m);
        ledger.Lines[0].Outstanding.ShouldBe(3500m);
        ledger.Balance.ShouldBe(16500m);

        var receipt = await tenant.GetAsync($"/api/v1/payments/{payment.Id}/receipt");
        receipt.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var pdf = PdfDocument.Open(await receipt.Content.ReadAsByteArrayAsync());
        var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        text.ShouldContain(verified.ReceiptNumber!);
        text.ShouldContain("3,000.00");
        text.ShouldContain(BillingPeriod.Of(FirstOfMonth.AddMonths(-2)).FirstDay.ToString("MMMM", CultureInfo.InvariantCulture));
        text.ShouldContain("16,500.00"); // balance still owing

        // Paying the rest clears the oldest month.
        var rest = await tenant.SubmitAsync(lease.Id, 3500m);
        await s.Landlord.PostAsync($"/api/v1/payments/{rest.Id}/verify", null);
        (await LedgerAsync(s.Landlord, lease.Id)).Lines[0].State.ShouldBe(ChargeState.Paid);
    }

    [Fact]
    public async Task BR09_A_payment_marked_for_a_month_pays_that_month()
    {
        var (s, lease) = await LeaseWithHistoryAsync();
        var latest = BillingPeriod.Of(Today).ToString();
        var payment = await (await s.TenantClientAsync()).SubmitAsync(lease.Id, 6500m, period: latest);

        await s.Landlord.PostAsync($"/api/v1/payments/{payment.Id}/verify", null);

        var ledger = await LedgerAsync(s.Landlord, lease.Id);
        ledger.Lines.Single(l => l.Period == latest).State.ShouldBe(ChargeState.Paid);
        ledger.Lines[0].Outstanding.ShouldBe(6500m);
    }

    [Fact]
    public async Task BR10_The_same_file_cannot_be_submitted_twice_unless_the_first_was_rejected()
    {
        var (s, lease) = await LeaseWithHistoryAsync();
        var tenant = await s.TenantClientAsync();
        var file = PaymentHelpers.UniquePdf();

        var first = await tenant.UploadPaymentAsync(lease.Id, 6500m, Today, file);
        await first.ShouldBeAsync(HttpStatusCode.Created);
        await (await tenant.UploadPaymentAsync(lease.Id, 6500m, Today, file, fileName: "renamed.pdf"))
            .ShouldBeAsync(HttpStatusCode.Conflict, "payment.duplicate");

        var id = (await first.ReadAsync<PaymentDto>()).Id;
        await (await s.Landlord.PostJsonAsync($"/api/v1/payments/{id}/reject", new RejectPaymentRequest("Amount doesn't match the bank statement")))
            .ShouldBeAsync(HttpStatusCode.OK);
        await (await tenant.UploadPaymentAsync(lease.Id, 6500m, Today, file)).ShouldBeAsync(HttpStatusCode.Created);
    }

    [Fact]
    public async Task BR11_Rejection_needs_a_reason_and_is_final()
    {
        var (s, lease) = await LeaseWithHistoryAsync();
        var payment = await (await s.TenantClientAsync()).SubmitAsync(lease.Id, 6500m);

        await (await s.Landlord.PostJsonAsync($"/api/v1/payments/{payment.Id}/reject", new RejectPaymentRequest("")))
            .ShouldBeAsync(HttpStatusCode.BadRequest, "validation");

        var rejected = await (await s.Landlord.PostJsonAsync($"/api/v1/payments/{payment.Id}/reject",
            new RejectPaymentRequest("Wrong account"))).ReadAsync<PaymentDto>();
        rejected.Status.ShouldBe(PaymentStatus.Rejected);
        rejected.RejectionReason.ShouldBe("Wrong account");

        await (await s.Landlord.PostAsync($"/api/v1/payments/{payment.Id}/verify", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "payment.already_reviewed");
    }

    [Fact]
    public async Task BR12_Files_are_checked_on_the_server()
    {
        var (s, lease) = await LeaseWithHistoryAsync();
        var tenant = await s.TenantClientAsync();

        var tooBig = PaymentHelpers.UniquePdf((int)(5 * 1024 * 1024) + 1);
        await (await tenant.UploadPaymentAsync(lease.Id, 100m, Today, tooBig))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "file.too_large");

        var exe = Encoding.ASCII.GetBytes("MZ\u0090\0 renamed Windows program");
        await (await tenant.UploadPaymentAsync(lease.Id, 100m, Today, exe, fileName: "payment.pdf"))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "file.type_not_allowed");

        var eicar = Encoding.ASCII.GetBytes(
            "%PDF-1.4\nX5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        await (await tenant.UploadPaymentAsync(lease.Id, 100m, Today, eicar))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "file.infected");

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(Guid.NewGuid().ToByteArray()).ToArray();
        var asPng = await (await tenant.UploadPaymentAsync(lease.Id, 100m, Today, png, fileName: "photo.jpeg",
            contentType: "application/octet-stream")).ReadAsync<PaymentDto>();
        asPng.ContentType.ShouldBe("image/png"); // typed from the bytes, not the name or the browser
        asPng.FileName.ShouldBe("photo.png");
    }

    [Fact]
    public async Task Payments_are_only_accepted_for_leases_that_have_started_and_months_in_their_term()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        var draft = await s.DraftAsync(Today.AddDays(5));
        await s.GenerateAsync(draft.Id);
        var tenant = await s.TenantClientAsync();

        await (await tenant.UploadPaymentAsync(draft.Id, 6500m, Today, PaymentHelpers.UniquePdf()))
            .ShouldBeAsync(HttpStatusCode.Conflict, "lease.not_payable");

        var (other, lease) = await LeaseWithHistoryAsync();
        await (await (await other.TenantClientAsync()).UploadPaymentAsync(lease.Id, 6500m, Today,
                PaymentHelpers.UniquePdf(), period: "2001-01"))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "period.outside_lease");
    }

    [Fact]
    public async Task BR01_Tenants_only_see_and_submit_for_their_own_leases()
    {
        var (mine, myLease) = await LeaseWithHistoryAsync();
        var (theirs, theirLease) = await LeaseWithHistoryAsync();
        var me = await mine.TenantClientAsync();
        var theirPayment = await (await theirs.TenantClientAsync()).SubmitAsync(theirLease.Id, 6500m);
        await me.SubmitAsync(myLease.Id, 6500m);

        (await me.GetAsync($"/api/v1/payments/{theirPayment.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.GetAsync($"/api/v1/payments/{theirPayment.Id}/file")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.GetAsync($"/api/v1/leases/{theirLease.Id}/ledger")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await me.UploadPaymentAsync(theirLease.Id, 1m, Today, PaymentHelpers.UniquePdf())).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);

        var list = await (await me.GetAsync("/api/v1/payments?pageSize=100")).ReadAsync<PagedResult<PaymentDto>>();
        list.Items.ShouldAllBe(p => p.LeaseId == myLease.Id);

        (await me.PostAsync($"/api/v1/payments/{theirPayment.Id}/verify", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await me.GetAsync("/api/v1/arrears")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Arrears_list_shows_overdue_leases_most_overdue_first()
    {
        var (s, lease) = await LeaseWithHistoryAsync();

        var arrears = await (await s.Landlord.GetAsync("/api/v1/arrears")).ReadAsync<List<ArrearsDto>>();

        var row = arrears.Single(a => a.LeaseId == lease.Id);
        row.Arrears.ShouldBeGreaterThanOrEqualTo(6500m);
        row.OldestOverduePeriod.ShouldBe(BillingPeriod.Of(FirstOfMonth.AddMonths(-2)).ToString());
        arrears.Select(a => a.DaysOverdue).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task BR08_Daily_checks_are_idempotent_and_record_each_overdue_reminder_once()
    {
        var (s, lease) = await LeaseWithHistoryAsync();

        await s.RunChecksAsync();
        var first = await LedgerAsync(s.Landlord, lease.Id);
        await s.RunChecksAsync();
        var second = await LedgerAsync(s.Landlord, lease.Id);

        second.Lines.Count.ShouldBe(first.Lines.Count);
        var overdue = first.Lines.Where(l => l.State == ChargeState.Overdue).ToList();
        overdue.ShouldNotBeEmpty();
        overdue.ShouldAllBe(l => l.OverdueNoticeOn == Today);
        second.Lines.Select(l => l.OverdueNoticeOn).ShouldBe(first.Lines.Select(l => l.OverdueNoticeOn));
    }

    [Fact]
    public async Task BR19_A_tenant_who_still_owes_rent_cannot_be_deleted_even_after_the_lease_ended()
    {
        var admin = await api.ClientForAsync(Seeded.Admin);
        var original = await (await admin.GetAsync("/api/v1/settings")).ReadAsync<SettingsDto>();
        try
        {
            await admin.PutJsonAsync("/api/v1/settings", LeaseApiTests.ToRequest(original) with { MonthToMonthOnLapse = false });
            var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
            var lease = await s.ActiveAsync(Today.AddMonths(-12).AddDays(-3));
            await s.RunChecksAsync();
            (await s.GetAsync(lease.Id)).Status.ShouldBe(LeaseStatus.Ended);

            await (await s.Landlord.DeleteAsync($"/api/v1/tenants/{s.TenantId}"))
                .ShouldBeAsync(HttpStatusCode.Conflict, "tenant.has_outstanding_balance");
        }
        finally
        {
            await admin.PutJsonAsync("/api/v1/settings", LeaseApiTests.ToRequest(original));
        }
    }

    [Fact]
    public async Task BR13_Deposit_is_received_then_deducted_and_refunded_only_after_the_lease_ends()
    {
        var admin = await api.ClientForAsync(Seeded.Admin);
        var original = await (await admin.GetAsync("/api/v1/settings")).ReadAsync<SettingsDto>();
        try
        {
            await admin.PutJsonAsync("/api/v1/settings", LeaseApiTests.ToRequest(original) with { MonthToMonthOnLapse = false });
            var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
            var lease = await s.ActiveAsync(Today.AddMonths(-12).AddDays(-3));
            var url = $"/api/v1/leases/{lease.Id}/deposit";

            var awaiting = await (await s.Landlord.GetAsync(url)).ReadAsync<DepositDto>();
            awaiting.Status.ShouldBe(DepositStatus.AwaitingPayment);
            awaiting.AmountAgreed.ShouldBe(6500m);
            awaiting.AllowedActions.ShouldBe(["receive"]);

            await (await s.Landlord.PostJsonAsync($"{url}/receive", new ReceiveDepositRequest(6500m, lease.StartDate)))
                .ShouldBeAsync(HttpStatusCode.OK);
            await (await s.Landlord.PostJsonAsync($"{url}/deductions", new DepositDeductionRequest(500m, "Broken window")))
                .ShouldBeAsync(HttpStatusCode.Conflict, "deposit.lease_not_ended");

            await s.RunChecksAsync(); // the lease lapses and ends (setting off)
            var ended = await (await s.Landlord.GetAsync(url)).ReadAsync<DepositDto>();
            ended.LeaseHasEnded.ShouldBeTrue();
            ended.RefundDueBy.ShouldBe(ended.LeaseEndedOn!.Value.AddDays(original.DepositRefundDays));
            ended.AllowedActions.ShouldBe(["deduct", "refund"]);

            await (await s.Landlord.PostJsonAsync($"{url}/deductions", new DepositDeductionRequest(7000m, "Too much")))
                .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "deposit.deduction_too_large");
            await s.Landlord.PostJsonAsync($"{url}/deductions", new DepositDeductionRequest(1200m, "Cleaning"));
            var refunded = await (await s.Landlord.PostJsonAsync($"{url}/refund", new RefundDepositRequest(Today)))
                .ReadAsync<DepositDto>();
            refunded.Status.ShouldBe(DepositStatus.PartiallyRefunded);
            refunded.AmountRefunded.ShouldBe(5300m);

            var tenantView = await (await (await s.TenantClientAsync()).GetAsync(url)).ReadAsync<DepositDto>();
            tenantView.Deductions.Single().Reason.ShouldBe("Cleaning");
            tenantView.AllowedActions.ShouldBeEmpty();
            (await (await s.TenantClientAsync()).PostJsonAsync($"{url}/refund", new RefundDepositRequest(Today)))
                .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await admin.PutJsonAsync("/api/v1/settings", LeaseApiTests.ToRequest(original));
        }
    }

    [Fact]
    public async Task Seeded_demo_rent_history_has_arrears_and_a_proof_waiting_for_review()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);

        var waiting = await (await landlord.GetAsync("/api/v1/payments?status=Submitted&pageSize=100"))
            .ReadAsync<PagedResult<PaymentDto>>();
        waiting.Items.ShouldContain(p => p.TenantName == "Sipho Dlamini");

        var arrears = await (await landlord.GetAsync("/api/v1/arrears")).ReadAsync<List<ArrearsDto>>();
        arrears.ShouldContain(a => a.TenantName == "Lerato Mokoena");
    }
}
