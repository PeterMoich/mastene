using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantManager.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef migrations add`. It never connects, so the placeholder connection
/// string is fine and the API's startup code (which needs secrets) is not run at design time.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=design-time-only;Database=TenantManager;Trusted_Connection=False")
            .Options;
        return new AppDbContext(options);
    }
}
