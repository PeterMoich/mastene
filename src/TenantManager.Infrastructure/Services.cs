using TenantManager.Application.Abstractions;

namespace TenantManager.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
