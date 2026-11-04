using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Auditing;
using TenantManager.Application.Files;
using TenantManager.Application.Leases;
using TenantManager.Application.Payments;
using TenantManager.Application.Properties;
using TenantManager.Application.Settings;
using TenantManager.Application.Tenants;

namespace TenantManager.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.AddScoped<PropertyService>();
        services.AddScoped<TenantService>();
        services.AddScoped<AuditService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<LeaseService>();
        services.AddScoped<LeaseDocumentFactory>();
        services.AddScoped<LeaseLifecycleService>();
        services.AddScoped<ITenantObligationsProvider, LeaseTenantObligationsProvider>();
        services.AddScoped<UploadValidator>();
        services.AddScoped<RentService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<ReceiptFactory>();
        services.AddScoped<DepositService>();
        return services;
    }
}
