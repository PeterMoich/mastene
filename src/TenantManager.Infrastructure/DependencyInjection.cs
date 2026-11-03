using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TenantManager.Application.Abstractions;
using TenantManager.Application.Accounts;
using TenantManager.Infrastructure.Identity;
using TenantManager.Infrastructure.Persistence;

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

        services.AddScoped<ITenantObligationsProvider, NoTenantObligationsProvider>();

        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.AddScoped<DataSeeder>();
        return services;
    }
}
