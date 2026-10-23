using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nexora.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to scaffold migrations; no database connection is made.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NexoraDbContext>
{
    public NexoraDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NexoraDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=nexora_design;Username=design;Password=design")
            .Options;
        return new NexoraDbContext(options);
    }
}
