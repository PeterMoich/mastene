using Microsoft.Extensions.Configuration;
using TenantManager.Application.Abstractions;

namespace TenantManager.Infrastructure;

/// <summary>Reads "Features:{name}" on every call, so a changed app setting takes effect without a redeploy.</summary>
public sealed class ConfigurationFeatureFlags(IConfiguration configuration) : IFeatureFlags
{
    public bool IsEnabled(string feature) => configuration.GetValue($"Features:{feature}", defaultValue: true);
}
