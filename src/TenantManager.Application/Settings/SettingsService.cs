using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TenantManager.Application.Abstractions;
using TenantManager.Domain.Leases;
using TenantManager.Domain.Payments;
using TenantManager.Domain.Settings;

namespace TenantManager.Application.Settings;

public sealed record SettingsRequest(
    int[] ExpiryNoticeDays,
    decimal DefaultEscalationPct,
    decimal MaxEscalationPct,
    bool MonthToMonthOnLapse,
    int TerminationNoticeDays,
    string LeaseTemplateVersion,
    int RentDueDay = 1,
    int RentGraceDays = 3,
    bool ProrateRent = true,
    int DepositRefundDays = 14);

/// <param name="LegalReviewNeeded">Names of the fields whose defaults are placeholders needing legal review.</param>
public sealed record SettingsDto(
    int[] ExpiryNoticeDays,
    decimal DefaultEscalationPct,
    decimal MaxEscalationPct,
    bool MonthToMonthOnLapse,
    int TerminationNoticeDays,
    string LeaseTemplateVersion,
    int RentDueDay,
    int RentGraceDays,
    bool ProrateRent,
    int DepositRefundDays,
    string[] LegalReviewNeeded);

public sealed class SettingsRequestValidator : AbstractValidator<SettingsRequest>
{
    public SettingsRequestValidator()
    {
        RuleFor(x => x.ExpiryNoticeDays).NotEmpty()
            .Must(d => d is not null && d.Length <= 5 && d.All(x => x is >= 1 and <= 365) && d.Distinct().Count() == d.Length)
            .WithMessage("Give one to five different reminder days, each between 1 and 365.");
        RuleFor(x => x.MaxEscalationPct).InclusiveBetween(0, 50).PrecisionScale(5, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.DefaultEscalationPct).GreaterThanOrEqualTo(0).LessThanOrEqualTo(x => x.MaxEscalationPct)
            .WithMessage("Default escalation must be between 0% and the maximum.")
            .PrecisionScale(5, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.TerminationNoticeDays).InclusiveBetween(0, 365);
        RuleFor(x => x.LeaseTemplateVersion).NotEmpty().MaximumLength(Lease.TemplateVersionMax);
        RuleFor(x => x.RentDueDay).InclusiveBetween(1, 28);
        RuleFor(x => x.RentGraceDays).InclusiveBetween(0, 31);
        RuleFor(x => x.DepositRefundDays).InclusiveBetween(0, 365);
    }
}

/// <summary>Environment-wide settings. Read by landlords, changed by admins.</summary>
public sealed class SettingsService(IApplicationDbContext db, IValidator<SettingsRequest> validator)
{
    private static readonly string[] LegalReviewFields =
        ["maxEscalationPct", "monthToMonthOnLapse", "terminationNoticeDays", "leaseTemplateVersion", "depositRefundDays"];

    public async Task<LeaseSettings> GetLeaseSettingsAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return row?.ToLeaseSettings() ?? LeaseSettings.Defaults;
    }

    public async Task<RentSettings> GetRentSettingsAsync(CancellationToken ct)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return row?.ToRentSettings() ?? RentSettings.Defaults;
    }

    public async Task<SettingsDto> GetAsync(CancellationToken ct) =>
        ToDto(await GetLeaseSettingsAsync(ct), await GetRentSettingsAsync(ct));

    public async Task<SettingsDto> UpdateAsync(SettingsRequest request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        var row = await db.Settings.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = BusinessSettings.CreateDefault();
            db.Settings.Add(row);
        }
        row.Apply(new LeaseSettings(request.ExpiryNoticeDays, request.DefaultEscalationPct, request.MaxEscalationPct,
            request.MonthToMonthOnLapse, request.TerminationNoticeDays, request.LeaseTemplateVersion));
        row.ApplyRent(new RentSettings(request.RentDueDay, request.RentGraceDays, request.ProrateRent,
            request.DepositRefundDays));
        await db.SaveChangesAsync(ct);
        return ToDto(row.ToLeaseSettings(), row.ToRentSettings());
    }

    private static SettingsDto ToDto(LeaseSettings s, RentSettings r) => new(
        s.ExpiryNoticeDays.ToArray(), s.DefaultEscalationPct, s.MaxEscalationPct, s.MonthToMonthOnLapse,
        s.TerminationNoticeDays, s.TemplateVersion, r.DueDay, r.GraceDays, r.ProrateFirstAndLastMonth,
        r.DepositRefundDays, LegalReviewFields);
}
