using System.Diagnostics;
using TenantManager.Application.Abstractions;
using TenantManager.Infrastructure.Identity;

namespace TenantManager.Api.Infrastructure;

/// <summary>Reads the caller from the validated JWT. Every value is null when there is no request (e.g. seeding).</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => ReadGuid(AppClaims.UserId);
    public string? Role => Principal?.FindFirst(AppClaims.Role)?.Value;
    public Guid? OwnerId => ReadGuid(AppClaims.OwnerId);
    public Guid? TenantId => ReadGuid(AppClaims.TenantId);

    public string? CorrelationId =>
        Activity.Current?.TraceId.ToString() ?? accessor.HttpContext?.TraceIdentifier;

    public string? DisplayName => Principal?.FindFirst(AppClaims.Name)?.Value;

    /// <summary>Behind nginx this is the client's address (forwarded headers are applied first).</summary>
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    private Guid? ReadGuid(string claim) =>
        Guid.TryParse(Principal?.FindFirst(claim)?.Value, out var id) ? id : null;
}
