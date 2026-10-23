using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Domain;
using Nexora.Infrastructure.Ad;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Wmi;

namespace Nexora.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNexoraInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var db = config.GetSection($"{NexoraOptions.SectionName}:Database").Get<DatabaseOptions>() ?? new DatabaseOptions();

        services.AddDbContext<NexoraDbContext>(o =>
        {
            if (string.Equals(db.Provider, "InMemory", StringComparison.OrdinalIgnoreCase))
            {
                o.UseInMemoryDatabase(db.InMemoryName)
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)); // the test provider has no transactions
                return;
            }
            var cs = config.GetConnectionString("Nexora")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Nexora is not configured. Run `nexora.ps1 -Action DbInit` or set ConnectionStrings__Nexora.");
            o.UseNpgsql(cs);
        });
        services.AddScoped<INexoraDb>(sp => sp.GetRequiredService<NexoraDbContext>());

        services.AddSingleton<IDirectoryLookup, AdDirectoryLookup>();
        services.AddSingleton<WmiEndpointCollector>();
        services.AddSingleton<IEndpointCollector>(sp => sp.GetRequiredService<WmiEndpointCollector>());
        services.AddSingleton<IPerformanceCollector>(sp => sp.GetRequiredService<WmiEndpointCollector>());
        services.AddSingleton<IServiceCollector>(sp => sp.GetRequiredService<WmiEndpointCollector>());
        services.AddSingleton<IProfileCollector>(sp => sp.GetRequiredService<WmiEndpointCollector>());
        services.AddSingleton<IHardwareInventoryCollector>(sp => sp.GetRequiredService<WmiEndpointCollector>());
        return services;
    }

    /// <summary>
    /// Makes sure the Nexora server's own machine is in the inventory and pinned, whatever was deleted or imported before.
    /// Runs at every start, so "delete all" can never leave the server out.
    /// </summary>
    public static async Task EnsureServerEndpointAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<INexoraDb>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var machine = Environment.MachineName;

        var existing = await db.Endpoints.FirstOrDefaultAsync(
            e => e.Hostname.ToLower() == machine.ToLower() || (e.ReportedHostname != null && e.ReportedHostname.ToLower() == machine.ToLower()), ct);
        if (existing is null)
        {
            db.Endpoints.Add(new ManagedEndpoint
            {
                Id = Guid.NewGuid(), Hostname = machine, DisplayName = machine, IsPinned = true, CreatedUtc = clock.UtcNow,
            });
        }
        else if (!existing.IsPinned)
        {
            existing.IsPinned = true;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Applies pending migrations (Npgsql) or creates the schema (InMemory).</summary>
    public static async Task InitializeNexoraDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<NexoraOptions>>().Value.Database;
        var ctx = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        if (string.Equals(opts.Provider, "InMemory", StringComparison.OrdinalIgnoreCase))
            await ctx.Database.EnsureCreatedAsync(ct);
        else if (opts.AutoMigrate)
            await ctx.Database.MigrateAsync(ct);

        // Stored health was scored by older rules: mark those PCs due, so the monitor recomputes it from the raw drive data on its next pass.
        var version = Nexora.Application.Services.DiskHealthEvaluator.ScoreVersion;
        try
        {
            await ctx.Endpoints.Where(e => ctx.DiskHealth.Any(d => d.EndpointId == e.Id && (d.ScoreVersion == null || d.ScoreVersion != version)))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastDiskPollUtc, (DateTime?)null), ct);
        }
        catch (InvalidOperationException) { /* providers without set-based updates (tests) have nothing stored to refresh */ }
    }
}
