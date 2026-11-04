using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Application.Files;
using TenantManager.Application.Leases;
using TenantManager.Application.Payments;
using TenantManager.Infrastructure.Documents;
using TenantManager.Infrastructure.Identity;
using TenantManager.Infrastructure.Jobs;
using TenantManager.Infrastructure.Persistence;
using TenantManager.Infrastructure.Storage;

namespace TenantManager.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("Default")
                               ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<AuditingInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3))
            .AddInterceptors(sp.GetRequiredService<AuditingInterceptor>()));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 12;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.AddSingleton(sp => new JwtSigningKey(
            sp.GetRequiredService<IOptions<JwtOptions>>(), allowGenerated: environment.IsDevelopment()));
        services.AddScoped<TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserAccountService, UserAccountService>();

        // Documents (ADR-0004, ADR-0005)
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        services.AddSingleton<ILeaseDocumentRenderer, QuestPdfLeaseRenderer>();
        services.AddSingleton<IReceiptRenderer, QuestPdfReceiptRenderer>();
        services.AddSingleton<IFileScanner, EicarOnlyFileScanner>();
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        var storageProvider = configuration[$"{StorageOptions.Section}:Provider"] ?? "Local";
        if (string.Equals(storageProvider, "AzureBlob", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IFileStorage, AzureBlobFileStorage>();
        else
            services.AddSingleton<IFileStorage, LocalFileStorage>();

        // Background jobs (ADR-0009). Off in tests and in the one-shot migrator.
        services.AddScoped<LeaseLifecycleJob>();
        if (configuration.GetValue<bool>("Jobs:Enabled"))
        {
            services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(() => new SqlConnection(connectionString), new SqlServerStorageOptions
                {
                    PrepareSchemaIfNecessary = true,
                    QueuePollInterval = TimeSpan.FromSeconds(15),
                }));
            services.AddHangfireServer(o => o.WorkerCount = 2);
        }

        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.AddScoped<DataSeeder>();
        return services;
    }
}
