using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TenantManager.Application.Auditing;
using TenantManager.Application.Properties;
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
        return services;
    }
}
