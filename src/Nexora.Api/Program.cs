using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.Options;
using Nexora.Api.Auth;
using Nexora.Api.Hubs;
using Nexora.Api.Http;
using Nexora.Api.Services;
using Nexora.Application;
using Nexora.Application.Jobs;
using Nexora.Application.Services;
using Nexora.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateLogger();

try
{
    // Anchor config/content to the exe folder so the working directory (service, script, shell) never matters.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "nexora-.log");
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(new CompactJsonFormatter())
        .WriteTo.File(new CompactJsonFormatter(), logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, shared: true)
        // Levels can be raised per namespace from configuration, e.g. Serilog__MinimumLevel__Override__Nexora.Infrastructure.Wmi=Debug to audit the drive mapping.
        .ReadFrom.Configuration(ctx.Configuration),
    preserveStaticLogger: true);

    var config = builder.Configuration;
    builder.Services.Configure<NexoraOptions>(config.GetSection(NexoraOptions.SectionName));
    builder.Services.Configure<FeatureOptions>(config.GetSection(FeatureOptions.SectionName));
    builder.Services.Configure<Nexora.Application.Services.HealthWeights>(config.GetSection("DiskHealth"));
    var nexora = config.GetSection(NexoraOptions.SectionName).Get<NexoraOptions>() ?? new NexoraOptions();

    // HTTP.sys authenticates in kernel mode with the machine account, so Kerberos works with the
    // host's existing HOST/<machine> SPN regardless of the process identity (no AD changes needed).
    // AllowAnonymous must be true so the anonymous /health endpoint can be reached; every other
    // endpoint is protected by the fallback authorization policy, whose challenge makes
    // HTTP.sys issue the Negotiate/NTLM 401.
    builder.WebHost.UseHttpSys(o =>
    {
        o.Authentication.Schemes = AuthenticationSchemes.Negotiate | AuthenticationSchemes.NTLM;
        o.Authentication.AllowAnonymous = true;
        o.UrlPrefixes.Add(nexora.Url);
    });

    builder.Services.AddAuthentication(HttpSysDefaults.AuthenticationScheme);
    builder.Services.AddAuthorization(Policies.Register);
    builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, NexoraAuthorizationResultHandler>();

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<AuthSchemeDetector>();
    builder.Services.AddSingleton<IAccessGroupService, AccessGroupService>();
    builder.Services.AddTransient<IClaimsTransformation, NexoraClaimsTransformation>();

    builder.Services.AddNexoraApplication();
    builder.Services.AddNexoraInfrastructure(config);

    builder.Services.AddAntiforgery(o =>
    {
        o.HeaderName = "X-XSRF-TOKEN";
        o.Cookie.Name = ".Nexora.Antiforgery";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
    builder.Services.AddDataProtection().SetApplicationName("Nexora");

    var permit = ActionRequestLimits.Validate(nexora.Actions.PermitPerMinute);
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("jobs", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ActionRequestLimits.Partition(ctx.User),
            _ => ActionRequestLimits.Options(permit)));
        // Profile deletion batches: confirm and delete together may be called only a few times a minute per operator.
        o.AddPolicy("profile-batch", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.GetSid() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });

    builder.Services.AddSignalR();
    builder.Services.AddSingleton<INexoraNotifier, SignalRNotifier>();
    builder.Services.AddSingleton<HelperRegistry>();
    builder.Services.AddSingleton<HubConnectionTracker>();
    builder.Services.AddSingleton<HelperDiagnostics>();
    builder.Services.AddSingleton(sp =>
    {
        var dataDir = config["Nexora:DataPath"] is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "data");
        return new ServerTrust(dataDir);
    });
    builder.Services.AddSingleton<JobSignal>();
    builder.Services.AddSingleton<IJobSignal>(sp => sp.GetRequiredService<JobSignal>());
    builder.Services.AddSingleton<IJobEvents, SignalRJobEvents>();
    builder.Services.AddSingleton<Nexora.Application.Diagnostics.IDiagnosisEvents, SignalRDiagnosisEvents>();
    if (string.Equals(nexora.Jobs.Executor, "Fake", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddSingleton<IJobExecutor, FakeJobExecutor>();
    else
        builder.Services.AddSingleton<IJobExecutor, HelperJobExecutor>();
    builder.Services.AddHostedService<JobRunner>();
    builder.Services.AddHostedService<MonitoringService>();

    builder.Services.AddControllers();

    var app = builder.Build();

    // The published helper carries this server's public key; stamp it now (skipped quietly while a helper is running from that file).
    {
        var trust = app.Services.GetRequiredService<ServerTrust>();
        var helperFile = Path.Combine(AppContext.BaseDirectory, "helper", "Nexora.Helper.exe");
        if (File.Exists(helperFile))
            app.Logger.LogInformation("Published helper stamped with server key {Fingerprint}: {Result}", trust.Fingerprint,
                Nexora.Contracts.TrustStamp.StampInPlace(helperFile, trust.PublicKey) ? "ok" : "file in use, the download is stamped on the fly");
    }

    app.Logger.LogWarning(
        "SECURITY NOTICE: Nexora is serving plain HTTP on {Url}. Traffic and Windows auth exchanges are NOT encrypted. " +
        "Credential operations stay disabled until HTTPS is introduced.", nexora.Url);
    if (string.IsNullOrWhiteSpace(nexora.SuperAdmin.Sid))
        app.Logger.LogWarning("Nexora:SuperAdmin:Sid is not configured; nobody can manage roles.");
    if (app.Services.GetRequiredService<IOptions<FeatureOptions>>().Value.CredentialOps)
        app.Logger.LogWarning("Features:CredentialOps is ON while serving plain HTTP. Turn it off.");

    // Fail fast: an invalid shipped diagnosis profile or collector (unknown id, unknown catalog action, duplicate rule, bad path or regex) stops the start with a clear message.
    _ = app.Services.GetRequiredService<Nexora.Application.Diagnostics.IDiagnosticProfiles>();
    if (app.Services.GetRequiredService<IOptions<FeatureOptions>>().Value.Diagnostics)
        app.Logger.LogInformation("Features:Diagnostics is ON. Helpers must carry the matching approved diagnostic collectors.");

    await app.Services.InitializeNexoraDatabaseAsync();
    await app.Services.EnsureServerEndpointAsync();

    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseMiddleware<HelperAttemptMiddleware>();

    // The SPA shell (index.html) is served by the fallback route below and requires sign-in;
    // hashed bundles under /assets carry no secrets and are served directly.
    app.UseWhen(
        c => !(c.Request.Path == "/" || c.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)),
        b => b.UseStaticFiles());

    app.UseAuthentication();
    app.UseMiddleware<CsrfMiddleware>();
    app.UseRateLimiter();
    app.UseAuthorization();

    // "app" lets a helper tell the Nexora server from any other application that answers on the same address (a wrong port).
    app.MapGet("/health", async (Nexora.Infrastructure.Persistence.NexoraDbContext db, CancellationToken ct) =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (await db.Database.CanConnectAsync(timeout.Token))
                return Results.Json(new { status = "ok", app = "nexora" });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            app.Logger.LogWarning(ex, "Database health check failed");
        }
        return Results.Json(new { status = "unavailable", app = "nexora" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }).AllowAnonymous();
    app.MapControllers();
    app.MapHub<NexoraHub>("/hubs/nexora");
    app.MapHub<HelperHub>("/hubs/helper");
    // The catch-all pattern below does not match the empty path, so the root gets its own route.
    app.MapGet("/", (HttpContext ctx, IWebHostEnvironment env) =>
        {
            var index = env.WebRootFileProvider.GetFileInfo("index.html");
            if (!index.Exists) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.File(index.CreateReadStream(), "text/html");
        })
        .RequireAuthorization(Policies.Authenticated);
    app.MapFallbackToFile(
            // Not for the API, the hubs, or real files: static files only run when no endpoint matched the request.
            "{**path:regex(^(?!api/|hubs/|internal/|health|assets/)(?!.*\\.[A-Za-z0-9]+$).*$)}", "index.html",
            new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" })
        .RequireAuthorization(Policies.Authenticated);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Nexora terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;

public partial class Program;
