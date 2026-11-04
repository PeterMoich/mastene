using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TenantManager.Application.Leases;
using TenantManager.Application.Messaging;
using TenantManager.Application.Payments;
using TenantManager.Application.Tenants;
using TenantManager.Domain.Messaging;
using TenantManager.Infrastructure.Messaging;
using TenantManager.Infrastructure.Persistence;

namespace TenantManager.Api.IntegrationTests;

public static class MessagingHelpers
{
    public static async Task<ThreadDto> ThreadAsync(this HttpClient client, Guid tenantId)
    {
        var response = await client.GetAsync($"/api/v1/conversations/{tenantId}");
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return await response.ReadAsync<ThreadDto>();
    }

    public static async Task ConsentAsync(this HttpClient tenant, Guid tenantId, bool granted = true) =>
        await (await tenant.PostJsonAsync($"/api/v1/tenants/{tenantId}/consents", new ConsentRequest(granted, null)))
            .ShouldBeAsync(HttpStatusCode.OK);

    /// <summary>A Meta-format webhook body.</summary>
    public static byte[] InboundPayload(string fromE164, string text, string? id = null) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    id = "TEST",
                    changes = new[]
                    {
                        new
                        {
                            field = "messages",
                            value = new
                            {
                                messages = new[]
                                {
                                    new
                                    {
                                        from = fromE164.TrimStart('+'), id = id ?? $"wamid.TEST{Guid.NewGuid():N}",
                                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        type = "text", text = new { body = text },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        }));

    public static byte[] StatusPayload(string providerMessageId, string status) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            entry = new[]
            {
                new { changes = new[] { new { value = new { statuses = new[] { new { id = providerMessageId, status } } } } } },
            },
        }));

    public static async Task<HttpResponseMessage> PostWebhookAsync(this HttpClient client, byte[] body, string? signature)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/whatsapp") { Content = content };
        if (signature is not null) request.Headers.TryAddWithoutValidation(WhatsAppSignature.Header, signature);
        return await client.SendAsync(request);
    }

    public static string Sign(byte[] body) => WhatsAppSignature.Sign(body, ApiFactory.WhatsAppSecret);
}

[Collection(ApiCollection.Name)]
public class MessagingApiTests(ApiFactory api)
{
    private async Task<(LeaseScenario S, LeaseDto Lease, HttpClient Tenant)> RunningLeaseAsync()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        var lease = await s.ActiveAsync(LeaseScenario.Today.AddMonths(-1));
        return (s, lease, await s.TenantClientAsync());
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = api.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static IEnumerable<MessageDto> WhatsApp(ThreadDto t) => t.Messages.Where(m => m.Channel == MessageChannel.WhatsApp);

    [Fact]
    public async Task BR16_Nothing_goes_to_WhatsApp_without_consent()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        var payment = await tenant.SubmitAsync(lease.Id, 6500m);
        await (await s.Landlord.PostAsync($"/api/v1/payments/{payment.Id}/verify", null)).ShouldBeAsync(HttpStatusCode.OK);

        WhatsApp(await s.Landlord.ThreadAsync(s.TenantId)).ShouldBeEmpty();
        await (await s.Landlord.PostJsonAsync($"/api/v1/conversations/{s.TenantId}/templates",
                new SendTemplateRequest("message_waiting", ["Lease", "Thandi", "your lease"])))
            .ShouldBeAsync(HttpStatusCode.Conflict, "consent.required");
    }

    [Fact]
    public async Task BR16_With_consent_a_verified_payment_sends_the_receipt_message()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        await tenant.ConsentAsync(s.TenantId);
        var payment = await tenant.SubmitAsync(lease.Id, 6500m);
        var verified = await (await s.Landlord.PostAsync($"/api/v1/payments/{payment.Id}/verify", null)).ReadAsync<PaymentDto>();

        var sent = WhatsApp(await tenant.ThreadAsync(s.TenantId)).ShouldHaveSingleItem();
        sent.Kind.ShouldBe(MessageKind.Template);
        sent.TemplateName.ShouldBe("payment_received");
        sent.Status.ShouldBe(MessageStatus.Sent);
        sent.Body.ShouldContain(verified.ReceiptNumber!);
        sent.Body.ShouldContain("R6,500.00");
    }

    [Fact]
    public async Task BR16_Consent_is_recorded_with_its_wording_and_the_landlord_must_say_how_it_was_given()
    {
        var (s, _, tenant) = await RunningLeaseAsync();
        await (await s.Landlord.PostJsonAsync($"/api/v1/tenants/{s.TenantId}/consents", new ConsentRequest(true, null)))
            .ShouldBeAsync(HttpStatusCode.UnprocessableEntity, "note.required");

        await tenant.ConsentAsync(s.TenantId);
        var status = await (await s.Landlord.GetAsync($"/api/v1/tenants/{s.TenantId}/consents")).ReadAsync<ConsentStatusDto>();
        status.Granted.ShouldBeTrue();
        status.Latest!.Source.ShouldBe(ConsentSource.Tenant);
        status.Latest.TextVersion.ShouldBe(ConsentText.CurrentVersion);

        // A new phone number needs new consent.
        await (await s.Landlord.PutJsonAsync($"/api/v1/tenants/{s.TenantId}",
            new TenantRequest("Lease", "Tester", s.TenantEmail, s.UniquePhone()))).ShouldBeAsync(HttpStatusCode.OK);
        (await s.Landlord.ThreadAsync(s.TenantId)).Consent.Granted.ShouldBeFalse();
    }

    [Fact]
    public async Task BR17_Free_form_replies_go_to_WhatsApp_only_inside_the_24_hour_window()
    {
        var (s, _, tenant) = await RunningLeaseAsync();
        await tenant.ConsentAsync(s.TenantId);

        // Before the tenant has written on WhatsApp: kept in the app.
        var before = await (await s.Landlord.PostJsonAsync($"/api/v1/conversations/{s.TenantId}/messages",
            new SendMessageRequest("Hello, just checking in."))).ReadAsync<ThreadDto>();
        before.WhatsAppTextAllowed.ShouldBeFalse();
        before.Messages.Last().Channel.ShouldBe(MessageChannel.App);

        // The tenant writes on WhatsApp (simulated through the signed webhook path): the window opens.
        var opened = await (await s.Landlord.PostJsonAsync("/api/v1/dev/whatsapp/inbound",
            new SimulateInboundRequest(s.TenantId, "Hi, is the plumber coming today?"))).ReadAsync<ThreadDto>();
        opened.InServiceWindow.ShouldBeTrue();
        opened.Messages.Last().Direction.ShouldBe(MessageDirection.Inbound);
        var inside = await (await s.Landlord.PostJsonAsync($"/api/v1/conversations/{s.TenantId}/messages",
            new SendMessageRequest("Yes, at 2pm."))).ReadAsync<ThreadDto>();
        inside.Messages.Last().Channel.ShouldBe(MessageChannel.WhatsApp);
        inside.Messages.Last().Status.ShouldBe(MessageStatus.Sent);

        // 25 hours later the window has closed again.
        await InDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Conversations SET LastWhatsAppInboundAt = DATEADD(hour, -25, LastWhatsAppInboundAt) WHERE TenantId = {s.TenantId}"));
        var after = await (await s.Landlord.PostJsonAsync($"/api/v1/conversations/{s.TenantId}/messages",
            new SendMessageRequest("Did he come?"))).ReadAsync<ThreadDto>();
        after.InServiceWindow.ShouldBeFalse();
        after.Messages.Last().Channel.ShouldBe(MessageChannel.App);

        // A template still reaches them.
        var template = await (await s.Landlord.PostJsonAsync($"/api/v1/conversations/{s.TenantId}/templates",
            new SendTemplateRequest("message_waiting", ["Lease", "Thandi", "the plumber's visit"]))).ReadAsync<ThreadDto>();
        template.Messages.Last().Status.ShouldBe(MessageStatus.Sent);
        template.Messages.Last().Body.ShouldContain("the plumber's visit");
    }

    [Fact]
    public async Task BR18_The_webhook_refuses_unsigned_or_wrongly_signed_calls()
    {
        var (s, _, _) = await RunningLeaseAsync();
        var client = api.CreateClient();
        var body = MessagingHelpers.InboundPayload(s.TenantPhone, "Forged message");

        (await client.PostWebhookAsync(body, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostWebhookAsync(body, WhatsAppSignature.Sign(body, "not-the-secret"))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostWebhookAsync(body, "sha256=zz")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await s.Landlord.ThreadAsync(s.TenantId)).Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task BR18_A_signed_inbound_message_is_stored_once_even_if_Meta_retries()
    {
        var (s, _, _) = await RunningLeaseAsync();
        var client = api.CreateClient();
        var body = MessagingHelpers.InboundPayload(s.TenantPhone, "My geyser is leaking");

        (await client.PostWebhookAsync(body, MessagingHelpers.Sign(body))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostWebhookAsync(body, MessagingHelpers.Sign(body))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var thread = await s.Landlord.ThreadAsync(s.TenantId);
        thread.Messages.ShouldHaveSingleItem().Body.ShouldBe("My geyser is leaking");
        thread.InServiceWindow.ShouldBeTrue();

        var list = await (await s.Landlord.GetAsync("/api/v1/conversations")).ReadAsync<List<ConversationSummaryDto>>();
        // Opening the thread above marked it read.
        list.Single(c => c.TenantId == s.TenantId).Unread.ShouldBe(0);
    }

    [Fact]
    public async Task The_webhook_handshake_echoes_the_challenge_only_for_the_right_token()
    {
        var client = api.CreateClient();
        var ok = await client.GetAsync(
            $"/api/v1/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token={ApiFactory.WhatsAppVerifyToken}&hub.challenge=12345");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadAsStringAsync()).ShouldBe("12345");

        (await client.GetAsync("/api/v1/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delivery_statuses_only_move_forward()
    {
        var (s, _, tenant) = await RunningLeaseAsync();
        await tenant.ConsentAsync(s.TenantId);
        await (await s.Landlord.PostJsonAsync($"/api/v1/conversations/{s.TenantId}/templates",
            new SendTemplateRequest("message_waiting", ["Lease", "Thandi", "the inspection"]))).ShouldBeAsync(HttpStatusCode.OK);
        var providerId = await InDbAsync(db => db.Messages.Where(m => m.TemplateName == "message_waiting" &&
                                                                        db.Conversations.Any(c => c.Id == m.ConversationId && c.TenantId == s.TenantId))
            .Select(m => m.ProviderMessageId!).SingleAsync());

        var client = api.CreateClient();
        foreach (var status in new[] { "read", "delivered" }) // out of order, as webhooks can be
        {
            var body = MessagingHelpers.StatusPayload(providerId, status);
            (await client.PostWebhookAsync(body, MessagingHelpers.Sign(body))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await s.Landlord.ThreadAsync(s.TenantId)).Messages.Last().Status.ShouldBe(MessageStatus.Read);
    }

    [Fact]
    public async Task BR16_Replying_STOP_on_WhatsApp_withdraws_consent()
    {
        var (s, _, tenant) = await RunningLeaseAsync();
        await tenant.ConsentAsync(s.TenantId);
        var client = api.CreateClient();
        var body = MessagingHelpers.InboundPayload(s.TenantPhone, "stop");

        (await client.PostWebhookAsync(body, MessagingHelpers.Sign(body))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var consent = await (await tenant.GetAsync($"/api/v1/tenants/{s.TenantId}/consents")).ReadAsync<ConsentStatusDto>();
        consent.Granted.ShouldBeFalse();
        consent.Latest!.Source.ShouldBe(ConsentSource.WhatsAppStop);
    }

    [Fact]
    public async Task Reminders_for_overdue_rent_are_sent_once_and_only_with_consent()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        var firstOfMonth = new DateOnly(LeaseScenario.Today.Year, LeaseScenario.Today.Month, 1);
        await s.ActiveAsync(firstOfMonth.AddMonths(-2)); // two months overdue
        var tenant = await s.TenantClientAsync();

        await s.RunChecksAsync();
        WhatsApp(await s.Landlord.ThreadAsync(s.TenantId)).ShouldBeEmpty();

        await tenant.ConsentAsync(s.TenantId);
        var summary = await (await s.Landlord.PostAsync("/api/v1/lease-checks/run", null)).ReadAsync<DailyChecksSummary>();
        summary.RemindersQueued.ShouldBeGreaterThanOrEqualTo(2);
        summary.MessagesSent.ShouldBeGreaterThanOrEqualTo(2);
        var overdue = WhatsApp(await s.Landlord.ThreadAsync(s.TenantId)).Where(m => m.TemplateName == "rent_overdue").ToList();
        overdue.Count.ShouldBeGreaterThanOrEqualTo(2);
        overdue.ShouldAllBe(m => m.Status == MessageStatus.Sent && m.Body.Contains("R6,500.00"));

        await s.RunChecksAsync();
        WhatsApp(await s.Landlord.ThreadAsync(s.TenantId)).Count(m => m.TemplateName == "rent_overdue").ShouldBe(overdue.Count);
    }

    [Fact]
    public async Task The_tenant_hears_on_WhatsApp_when_their_issue_moves_on()
    {
        var (s, lease, tenant) = await RunningLeaseAsync();
        await tenant.ConsentAsync(s.TenantId);
        var issue = await tenant.LogIssueAsync(lease.Id);

        await (await s.Landlord.PostAsync($"/api/v1/issues/{issue.Id}/acknowledge", null)).ShouldBeAsync(HttpStatusCode.OK);
        await (await s.Landlord.PostAsync($"/api/v1/issues/{issue.Id}/start", null)).ShouldBeAsync(HttpStatusCode.OK);

        var updates = WhatsApp(await tenant.ThreadAsync(s.TenantId)).Where(m => m.TemplateName == "issue_update").ToList();
        updates.Count.ShouldBe(2);
        updates[0].Body.ShouldContain("Geyser not heating");
        updates[1].Body.ShouldContain("work has started");
    }

    [Fact]
    public async Task A_failed_send_is_kept_with_its_reason_and_retried()
    {
        var s = await LeaseScenario.CreateAsync(api, await api.ClientForAsync(Seeded.Landlord));
        await (await s.Landlord.PutJsonAsync($"/api/v1/tenants/{s.TenantId}",
            new TenantRequest("Lease", "Tester", s.TenantEmail, "+27820000000"))).ShouldBeAsync(HttpStatusCode.OK);
        var tenant = await s.TenantClientAsync();
        await tenant.ConsentAsync(s.TenantId);

        var thread = await (await s.Landlord.PostJsonAsync($"/api/v1/conversations/{s.TenantId}/templates",
            new SendTemplateRequest("message_waiting", ["Lease", "Thandi", "your lease"]))).ReadAsync<ThreadDto>();

        var message = thread.Messages.Last();
        message.Status.ShouldBe(MessageStatus.Queued); // will be retried
        message.Error.ShouldNotBeNull().ShouldContain("not on WhatsApp");
    }

    [Fact]
    public async Task BR01_Tenants_see_only_their_own_thread_and_can_write_in_the_app()
    {
        var (s, _, tenant) = await RunningLeaseAsync();
        var other = await api.ClientForAsync(Seeded.OtherTenantWithLogin);

        (await other.GetAsync($"/api/v1/conversations/{s.TenantId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await tenant.GetAsync("/api/v1/conversations")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await tenant.PostJsonAsync("/api/v1/dev/whatsapp/inbound", new SimulateInboundRequest(s.TenantId, "x")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var mine = await (await tenant.PostJsonAsync($"/api/v1/conversations/{s.TenantId}/messages",
            new SendMessageRequest("The gate remote isn't working."))).ReadAsync<ThreadDto>();
        mine.Messages.Last().Channel.ShouldBe(MessageChannel.App);
        mine.SimulationAvailable.ShouldBeFalse();

        var list = await (await s.Landlord.GetAsync("/api/v1/conversations")).ReadAsync<List<ConversationSummaryDto>>();
        list.Single(c => c.TenantId == s.TenantId).Unread.ShouldBe(1);
        list.First().Unread.ShouldBeGreaterThan(0); // unread threads come first
    }

    [Fact]
    public async Task Seeded_demo_thread_is_inside_the_window_with_consent()
    {
        var landlord = await api.ClientForAsync(Seeded.Landlord);
        var list = await (await landlord.GetAsync("/api/v1/conversations")).ReadAsync<List<ConversationSummaryDto>>();
        var sipho = list.Single(c => c.TenantName == "Sipho Dlamini");
        sipho.ConsentGranted.ShouldBeTrue();
        sipho.InServiceWindow.ShouldBeTrue();
    }

    [Fact]
    public async Task Only_an_admin_gets_a_jobs_dashboard_link()
    {
        (await (await api.ClientForAsync(Seeded.Landlord)).PostAsync("/api/v1/admin/jobs-dashboard", null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // Jobs are switched off in the test host, so even an admin is told so.
        await (await (await api.ClientForAsync(Seeded.Admin)).PostAsync("/api/v1/admin/jobs-dashboard", null))
            .ShouldBeAsync(HttpStatusCode.Conflict, "jobs.disabled");
    }
}

/// <summary>The Meta provider's requests, checked against a stub HTTP handler (no network).</summary>
public class MetaCloudWhatsAppProviderTests
{
    private sealed class Stub(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static (MetaCloudWhatsAppProvider Provider, Stub Stub) Create(HttpStatusCode status, string response)
    {
        var stub = new Stub(status, response);
        var options = Options.Create(new WhatsAppOptions
        {
            Provider = "Meta", AccessToken = "token-123", PhoneNumberId = "1098765", GraphApiVersion = "v21.0",
        });
        return (new MetaCloudWhatsAppProvider(new HttpClient(stub), options, NullLogger<MetaCloudWhatsAppProvider>.Instance), stub);
    }

    [Fact]
    public async Task Sends_a_template_with_its_body_parameters()
    {
        var (provider, stub) = Create(HttpStatusCode.OK, """{"messages":[{"id":"wamid.ABC"}]}""");

        var result = await provider.SendTemplateAsync("+27825550101", "rent_overdue", "en", ["Lerato", "October 2026", "R3,500.00"],
            CancellationToken.None);

        result.ShouldBe(WhatsAppSendResult.Sent("wamid.ABC"));
        stub.Request!.RequestUri!.ToString().ShouldBe("https://graph.facebook.com/v21.0/1098765/messages");
        stub.Request.Headers.Authorization!.ToString().ShouldBe("Bearer token-123");
        using var json = JsonDocument.Parse(stub.Body!);
        var root = json.RootElement;
        root.GetProperty("to").GetString().ShouldBe("27825550101");
        root.GetProperty("type").GetString().ShouldBe("template");
        var template = root.GetProperty("template");
        template.GetProperty("name").GetString().ShouldBe("rent_overdue");
        template.GetProperty("language").GetProperty("code").GetString().ShouldBe("en");
        template.GetProperty("components")[0].GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("text").GetString()).ShouldBe(["Lerato", "October 2026", "R3,500.00"]);
    }

    [Fact]
    public async Task Reports_Meta_errors_instead_of_throwing()
    {
        var (provider, _) = Create(HttpStatusCode.BadRequest,
            """{"error":{"message":"Message failed to send because more than 24 hours have passed","code":131047}}""");

        var result = await provider.SendTextAsync("+27825550101", "Hello", CancellationToken.None);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldBe("131047: Message failed to send because more than 24 hours have passed");
    }
}
