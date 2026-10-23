using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Application.Services;

namespace Nexora.Application;

public sealed class FeatureGate(IOptions<FeatureOptions> options) : IFeatureGate
{
    public bool CredentialOpsEnabled => options.Value.CredentialOps;
}

public static class DependencyInjection
{
    public static IServiceCollection AddNexoraApplication(this IServiceCollection services)
    {
        services.AddMemoryCacheIfMissing();
        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddSingleton<ISuperAdminChecker, SuperAdminChecker>();
        services.AddSingleton<IFeatureGate, FeatureGate>();
        services.AddSingleton<IUserAccessCache, UserAccessCache>();

        services.AddScoped<IMeService, MeService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAccessRequestService, AccessRequestService>();
        services.AddScoped<IRoleManagementService, RoleManagementService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IEndpointDtoFactory, EndpointDtoFactory>();
        services.AddScoped<IEndpointService, EndpointService>();
        services.AddSingleton<DetailReadGate>();
        services.AddSingleton<LivePerformanceReader>();
        services.AddScoped<IEndpointExportService, EndpointExportService>();
        services.AddScoped<IDiskHealthService, DiskHealthService>();
        services.AddSingleton<HardwareInventoryCache>();
        services.AddSingleton<OuBackfillService>();
        services.AddScoped<IHardwareInventoryService, HardwareInventoryService>();
        services.AddScoped<IDiskAuditService, DiskAuditService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<IMetricsService, MetricsService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IScriptCatalogService, ScriptCatalogService>();
        services.AddSingleton<Jobs.IConfirmTokens, Jobs.ConfirmTokens>();
        services.AddSingleton<Jobs.IDenialRateLimiter, Jobs.DenialRateLimiter>();
        services.AddSingleton<IServiceReader, ServiceReader>();
        services.AddScoped<IServiceGuard, ServiceGuard>();
        services.AddScoped<IServiceQuery, ServiceQuery>();
        services.AddSingleton<Jobs.IRiskEscalator, Jobs.ServiceRiskEscalator>();
        services.AddScoped<Jobs.IJobDenialAuditor, Jobs.JobDenialAuditor>();
        services.AddScoped<Jobs.JobService>();
        services.AddScoped<Jobs.IJobService>(sp => sp.GetRequiredService<Jobs.JobService>());
        services.AddScoped<Jobs.IJobReports>(sp => sp.GetRequiredService<Jobs.JobService>());
        services.AddScoped<Jobs.IProfileBatchService>(sp => sp.GetRequiredService<Jobs.JobService>());
        services.AddSingleton<IProfileReader, ProfileReader>();
        services.AddScoped<IProfileQuery, ProfileQuery>();
        services.AddScoped<Jobs.JobOrchestrator>();

        // F5 diagnosis: the shipped collectors and profiles are validated when first resolved; Program resolves them at startup so a bad profile stops the server.
        services.AddSingleton<Diagnostics.IDiagnosticCollectors>(_ => Diagnostics.CollectorCatalog.FromAssembly(typeof(DependencyInjection).Assembly));
        services.AddSingleton<Diagnostics.IDiagnosticProfiles>(sp =>
            Diagnostics.ProfileCatalog.FromAssembly(typeof(DependencyInjection).Assembly, sp.GetRequiredService<Diagnostics.IDiagnosticCollectors>().Ids));
        services.AddSingleton<Diagnostics.IRuleEngine, Diagnostics.RuleEngine>();
        services.AddScoped<Diagnostics.IDiagnosisCompletion, Diagnostics.DiagnosisCompletion>();
        services.AddScoped<Diagnostics.IDiagnosisService, Diagnostics.DiagnosisService>();
        services.AddScoped<Diagnostics.IDiagnosticJobStarter>(sp => sp.GetRequiredService<Jobs.JobService>());
        return services;
    }

    private static void AddMemoryCacheIfMissing(this IServiceCollection services) =>
        services.AddMemoryCache();
}
