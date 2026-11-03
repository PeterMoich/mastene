using System.Threading.RateLimiting;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using TenantManager.Application.Abstractions;
using TenantManager.Infrastructure.Identity;

namespace TenantManager.Api.Infrastructure;

public static class Policies
{
    public const string Admin = "AdminOnly";
    public const string Landlord = "LandlordOnly";
    public const string AdminOrLandlord = "AdminOrLandlord";
    public const string LandlordOrTenant = "LandlordOrTenant";
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>Login/refresh attempts per client IP per minute.</summary>
    public int AuthPerMinute { get; set; } = 10;

    /// <summary>All other requests per user (or IP when anonymous) per minute.</summary>
    public int GeneralPerMinute { get; set; } = 300;
}

public static class ApiSetup
{
    public static IServiceCollection AddApiSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtSigningKey, IOptions<JwtOptions>>((options, key, jwt) =>
            {
                options.MapInboundClaims = false; // keep "sub", "role" etc. exactly as issued
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = key.Key,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = AppClaims.Name,
                    RoleClaimType = AppClaims.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Policies.Landlord, p => p.RequireRole(Roles.Landlord))
            .AddPolicy(Policies.AdminOrLandlord, p => p.RequireRole(Roles.Admin, Roles.Landlord))
            .AddPolicy(Policies.LandlordOrTenant, p => p.RequireRole(Roles.Landlord, Roles.Tenant))
            // Secure by default: an endpoint without [AllowAnonymous] always needs a signed-in user.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        var limits = configuration.GetSection(RateLimitOptions.Section).Get<RateLimitOptions>() ?? new RateLimitOptions();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicies.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AuthPerMinute, Window = TimeSpan.FromMinutes(1) }));
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ctx.User.FindFirst(AppClaims.UserId)?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.GeneralPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });

        return services;
    }

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddApiVersioning(o =>
            {
                o.DefaultApiVersion = new ApiVersion(1, 0);
                o.ReportApiVersions = true;
                o.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddMvc()
            .AddApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'VVV";
                o.SubstituteApiVersionInUrl = true;
            });

        services.AddSwaggerGen(o =>
        {
            o.SwaggerDoc("v1", new OpenApiInfo { Title = "Tenant Manager API", Version = "v1" });
            o.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Paste the accessToken from POST /api/v1/auth/login.",
            });
            o.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("bearer", document)] = [],
            });
        });
        return services;
    }
}
