using System.Security.Cryptography;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.DataProtection;

namespace TenantManager.Api.Infrastructure;

/// <summary>
/// The Hangfire dashboard at /hangfire, for Admins only. The dashboard is a server-rendered page, so it
/// can't send the app's bearer token. Instead an Admin asks the API for a ticket (valid two minutes),
/// opens /hangfire?ticket=..., and gets an HttpOnly, SameSite=Strict cookie for /hangfire only, valid
/// 30 minutes. Both are encrypted and signed with ASP.NET Core Data Protection.
/// </summary>
public static class JobsDashboard
{
    public const string Path = "/hangfire";
    private const string CookieName = "tm_jobs";
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    private static ITimeLimitedDataProtector Protector(IServiceProvider services, string purpose) =>
        services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("TenantManager.JobsDashboard", purpose).ToTimeLimitedDataProtector();

    public static string CreateTicket(IServiceProvider services, Guid adminUserId) =>
        Protector(services, "ticket").Protect(adminUserId.ToString(), TicketLifetime);

    public static void UseJobsDashboard(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Path) && context.Request.Query.TryGetValue("ticket", out var ticket))
            {
                try
                {
                    var userId = Protector(context.RequestServices, "ticket").Unprotect(ticket.ToString());
                    context.Response.Cookies.Append(CookieName,
                        Protector(context.RequestServices, "session").Protect(userId, SessionLifetime),
                        new CookieOptions
                        {
                            HttpOnly = true,
                            SameSite = SameSiteMode.Strict,
                            Secure = context.Request.IsHttps,
                            Path = Path,
                            MaxAge = SessionLifetime,
                        });
                    context.Response.Redirect(Path); // drop the ticket from the address bar and history
                }
                catch (CryptographicException)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("This link has expired. Open the jobs dashboard again from Settings.");
                }
                return;
            }
            await next();
        });

        app.UseHangfireDashboard(Path, new DashboardOptions
        {
            Authorization = [new SessionCookieFilter()],
            DashboardTitle = "Tenant Manager background jobs",
            AppPath = "/settings",
        });
    }

    private sealed class SessionCookieFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context)
        {
            var http = context.GetHttpContext();
            if (!http.Request.Cookies.TryGetValue(CookieName, out var cookie)) return false;
            try
            {
                return Guid.TryParse(Protector(http.RequestServices, "session").Unprotect(cookie), out _);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }
}
