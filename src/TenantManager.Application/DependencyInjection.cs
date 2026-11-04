using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Auditing;
using TenantManager.Application.Files;
using TenantManager.Application.Inspections;
using TenantManager.Application.Issues;
using TenantManager.Application.Leases;
using TenantManager.Application.Messaging;
using TenantManager.Application.Payments;
using TenantManager.Application.Privacy;
using TenantManager.Application.Properties;
using TenantManager.Application.Settings;
using TenantManager.Application.Tenants;

namespace TenantManager.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.AddScoped<AuditRedaction>();
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
        services.AddScoped<IssueService>();
        services.AddScoped<IssueSlaService>();
        services.AddScoped<ContractorService>();
        services.AddScoped<InspectionService>();
        services.AddScoped<Notifier>();
        services.AddScoped<MessageDispatcher>();
        services.AddScoped<WhatsAppWebhookService>();
        services.AddScoped<MessagingService>();
        services.AddScoped<ReminderService>();
        services.AddScoped<PrivacyService>();
        return services;
    }
}
