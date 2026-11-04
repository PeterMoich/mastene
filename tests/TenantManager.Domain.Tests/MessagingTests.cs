using Shouldly;
using TenantManager.Domain.Common;
using TenantManager.Domain.Messaging;

namespace TenantManager.Domain.Tests;

public class ConsentTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
    private const string Phone = "+27825550101";

    [Fact]
    public void BR16_No_record_means_no_messages()
    {
        ConsentRecord.Allows(null, Phone).ShouldBeFalse();
    }

    [Fact]
    public void BR16_A_grant_allows_messages_and_records_the_wording_version()
    {
        var grant = ConsentRecord.Grant(Owner, Tenant, Phone, ConsentSource.Tenant, "Sipho Dlamini", Guid.NewGuid(), null, Now);

        ConsentRecord.Allows(grant, Phone).ShouldBeTrue();
        grant.TextVersion.ShouldBe(ConsentText.CurrentVersion);
    }

    [Fact]
    public void BR16_The_latest_record_decides_and_a_withdrawal_stops_messages()
    {
        var withdrawn = ConsentRecord.Withdraw(Owner, Tenant, Phone, ConsentSource.WhatsAppStop, "Sipho Dlamini", null, null, Now);
        ConsentRecord.Allows(withdrawn, Phone).ShouldBeFalse();
    }

    [Fact]
    public void BR16_Consent_is_for_one_phone_number()
    {
        var grant = ConsentRecord.Grant(Owner, Tenant, Phone, ConsentSource.Tenant, "Sipho", null, null, Now);
        ConsentRecord.Allows(grant, "+27835550199").ShouldBeFalse();
    }

    [Fact]
    public void BR16_The_landlord_must_say_how_consent_was_given()
    {
        Should.Throw<DomainRuleException>(() =>
                ConsentRecord.Grant(Owner, Tenant, Phone, ConsentSource.Landlord, "Thandi", null, " ", Now))
            .Code.ShouldBe("note.required");
        ConsentRecord.Grant(Owner, Tenant, Phone, ConsentSource.Landlord, "Thandi", null, "Signed form, 3 Nov 2026", Now)
            .Granted.ShouldBeTrue();
    }
}

public class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    private static Conversation WithInbound(DateTimeOffset at, MessageChannel channel = MessageChannel.WhatsApp)
    {
        var c = new Conversation(Guid.NewGuid(), Guid.NewGuid());
        var m = channel == MessageChannel.WhatsApp
            ? Message.InboundWhatsApp(c.Id, "Hi, the tap is still leaking", "wamid.1", at, "Sipho")
            : Message.InboundApp(c.Id, "Hi from the app", at, "Sipho");
        c.Touch(m);
        return c;
    }

    [Fact]
    public void BR17_The_window_is_open_for_24_hours_after_the_tenant_last_wrote_on_WhatsApp()
    {
        var c = WithInbound(Now);

        c.InServiceWindow(Now.AddHours(23).AddMinutes(59)).ShouldBeTrue();
        c.InServiceWindow(Now.AddHours(24)).ShouldBeFalse();
        c.ServiceWindowEndsAt.ShouldBe(Now.AddHours(24));
    }

    [Fact]
    public void BR17_No_window_before_the_tenant_writes_on_WhatsApp_and_app_messages_do_not_open_it()
    {
        new Conversation(Guid.NewGuid(), Guid.NewGuid()).InServiceWindow(Now).ShouldBeFalse();
        WithInbound(Now, MessageChannel.App).InServiceWindow(Now.AddMinutes(1)).ShouldBeFalse();
    }

    [Fact]
    public void Inbound_messages_count_as_unread_for_the_landlord_and_update_the_preview()
    {
        var c = WithInbound(Now);
        c.UnreadByLandlord.ShouldBe(1);
        c.LastMessagePreview.ShouldBe("Hi, the tap is still leaking");
        c.MarkReadByLandlord();
        c.UnreadByLandlord.ShouldBe(0);
    }

    [Fact]
    public void BR17_A_free_form_reply_outside_the_window_is_kept_in_the_app()
    {
        var c = new Conversation(Guid.NewGuid(), Guid.NewGuid());
        var inApp = Message.OutboundText(c.Id, "Please call me", toWhatsApp: false, Now, "Thandi");
        inApp.Channel.ShouldBe(MessageChannel.App);
        inApp.Status.ShouldBe(MessageStatus.Received);

        var whatsApp = Message.OutboundText(c.Id, "Please call me", toWhatsApp: true, Now, "Thandi");
        whatsApp.Channel.ShouldBe(MessageChannel.WhatsApp);
        whatsApp.Status.ShouldBe(MessageStatus.Queued);
    }
}

public class MessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    private static Message Queued() =>
        Message.OutboundTemplate(Guid.NewGuid(), MessageTemplates.PaymentReceived.Render(["Sipho", "R6,500.00", "R2026-0001"]),
            Now, "System", "payment-received:1");

    [Fact]
    public void Statuses_only_move_forward_even_when_webhooks_arrive_out_of_order()
    {
        var m = Queued();
        m.BeginAttempt(Now);
        m.MarkSent("wamid.1", Now);
        m.ApplyProviderStatus(MessageStatus.Read, null).ShouldBeTrue();
        m.ApplyProviderStatus(MessageStatus.Delivered, null).ShouldBeFalse();
        m.Status.ShouldBe(MessageStatus.Read);
    }

    [Fact]
    public void A_failure_is_recorded_with_its_reason()
    {
        var m = Queued();
        m.BeginAttempt(Now);
        m.MarkSent("wamid.1", Now);
        m.ApplyProviderStatus(MessageStatus.Failed, "Number not on WhatsApp").ShouldBeTrue();
        m.Status.ShouldBe(MessageStatus.Failed);
        m.Error.ShouldBe("Number not on WhatsApp");
    }

    [Fact]
    public void Sending_is_retried_then_given_up()
    {
        var m = Queued();
        for (var i = 0; i < 2; i++)
        {
            m.BeginAttempt(Now);
            m.RecordFailure("timeout", maxAttempts: 3);
        }
        m.Status.ShouldBe(MessageStatus.Queued);
        m.BeginAttempt(Now);
        m.RecordFailure("timeout", maxAttempts: 3);
        m.Status.ShouldBe(MessageStatus.Failed);
        m.Attempts.ShouldBe(3);
    }

    [Fact]
    public void Inbound_messages_ignore_delivery_statuses()
    {
        Message.InboundWhatsApp(Guid.NewGuid(), "Hi", "wamid.2", Now, "Sipho")
            .ApplyProviderStatus(MessageStatus.Read, null).ShouldBeFalse();
    }
}

public class TemplateTests
{
    [Fact]
    public void A_template_is_filled_in_in_order()
    {
        var t = MessageTemplates.RentOverdue.Render(["Lerato", "October 2026", "R3,500.00"]);

        t.Body.ShouldBe("Hi Lerato, your rent for October 2026 is overdue and R3,500.00 is still outstanding. " +
                        "Please pay, or upload your proof of payment in the Tenant Manager app.");
        t.Parameters.ShouldBe(["Lerato", "October 2026", "R3,500.00"]);
    }

    [Fact]
    public void Every_parameter_is_needed_and_new_lines_are_flattened_for_WhatsApp()
    {
        Should.Throw<DomainRuleException>(() => MessageTemplates.RentOverdue.Render(["Lerato"])).Code.ShouldBe("template.parameters");
        Should.Throw<DomainRuleException>(() => MessageTemplates.RentOverdue.Render(["Lerato", " ", "R1"])).Code.ShouldBe("template.parameters");
        MessageTemplates.MessageWaiting.Render(["Sipho", "Thandi", "the\nwater   bill"]).Parameters[2].ShouldBe("the water bill");
    }

    [Fact]
    public void Every_template_uses_exactly_its_parameters()
    {
        foreach (var t in MessageTemplates.All)
        {
            var values = t.ParameterNames.Select((_, i) => $"v{i + 1}").ToList();
            var body = t.Render(values).Body;
            body.ShouldNotContain("{{");
            values.ShouldAllBe(v => body.Contains(v));
        }
    }

    [Fact]
    public void Unknown_templates_are_refused()
    {
        Should.Throw<DomainRuleException>(() => MessageTemplates.Find("free_money")).Code.ShouldBe("template.unknown");
    }
}
