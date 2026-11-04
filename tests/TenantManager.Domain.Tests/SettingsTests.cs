using Shouldly;
using TenantManager.Application.Settings;
using TenantManager.Domain.Common;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Settings;

namespace TenantManager.Domain.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_round_trip_through_the_stored_form()
    {
        var stored = BusinessSettings.CreateDefault();

        stored.ExpiryNoticeDays.ShouldBe("90,60,30");
        var read = stored.ToLeaseSettings();
        read.ExpiryNoticeDays.ShouldBe([90, 60, 30]);
        (read with { ExpiryNoticeDays = LeaseSettings.Defaults.ExpiryNoticeDays }).ShouldBe(LeaseSettings.Defaults);
    }

    [Fact]
    public void Notice_days_are_stored_largest_first()
    {
        var stored = BusinessSettings.CreateDefault();
        stored.Apply(LeaseSettings.Defaults with { ExpiryNoticeDays = [14, 60] });
        stored.ExpiryNoticeDays.ShouldBe("60,14");
        stored.ToLeaseSettings().FirstNoticeDays.ShouldBe(60);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 30, 30 })]
    [InlineData(new[] { 400 })]
    public void Invalid_notice_days_are_rejected(int[] days)
    {
        Should.Throw<DomainRuleException>(() =>
            BusinessSettings.CreateDefault().Apply(LeaseSettings.Defaults with { ExpiryNoticeDays = days }));
    }

    [Fact]
    public void Default_escalation_cannot_exceed_the_maximum()
    {
        Should.Throw<DomainRuleException>(() => BusinessSettings.CreateDefault()
                .Apply(LeaseSettings.Defaults with { DefaultEscalationPct = 12m, MaxEscalationPct = 10m }))
            .Code.ShouldBe("defaultEscalationPct.invalid");

        new SettingsRequestValidator().Validate(new SettingsRequest([90], 12m, 10m, true, 20, "v0-test"))
            .Errors.ShouldContain(e => e.PropertyName == "DefaultEscalationPct");
    }
}
