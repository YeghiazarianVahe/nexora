using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Nexora.Api.Auth;
using Nexora.Api.Hubs;
using Nexora.Api.Http;
using Nexora.Api.Services;
using Nexora.Application;
using Nexora.Application.Services;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Api.Controllers;

[ApiController]
[Route("api/helper")]
[Authorize(Policy = Policies.Operator)]
public sealed class HelperController(HelperRegistry registry, HelperDiagnostics diagnostics, ServerTrust trust, IOptions<NexoraOptions> options, ILogger<HelperController> logger) : ControllerBase
{
    /// <summary>Whether this operator's helper is connected now, whether it ever was (since the server started), and the last attempt with its outcome.</summary>
    [HttpGet("status")]
    public object Status()
    {
        var sid = User.GetSid()!;
        var snap = diagnostics.Snapshot(sid);
        return new
        {
            connected = registry.Find(sid) is not null,
            everConnected = snap.EverConnected,
            lastConnectedUtc = snap.LastConnectedUtc,
            lastDisconnectedUtc = snap.LastDisconnectedUtc,
            lastAttempt = snap.LastAttempt,
            serverUrl = options.Value.ResolvePublicUrl(),
            downloadAvailable = System.IO.File.Exists(HelperFile),
        };
    }

    private static string HelperFile => Path.Combine(AppContext.BaseDirectory, "helper", "Nexora.Helper.exe");

    /// <summary>
    /// The helper itself, self-contained (no .NET install needed), with this server's public key stamped into it. The key in the file is what the
    /// helper checks every job against, so a helper must come from here (or from a copy of this exact file).
    /// </summary>
    [HttpGet("download")]
    public async Task Download(CancellationToken ct)
    {
        if (!System.IO.File.Exists(HelperFile)) { Response.StatusCode = 404; await Response.WriteAsJsonAsync(new { error = "The helper is not published on this server yet." }, ct); return; }
        logger.LogInformation("Helper downloaded by {Account}", User.GetAccount());
        var (pinned, original) = TrustStamp.Read(HelperFile);
        if (TrustStamp.HasCertificateTable(HelperFile))
        {
            if (pinned is null || !pinned.AsSpan().SequenceEqual(trust.PublicKey))
            {
                Response.StatusCode=503;
                await Response.WriteAsJsonAsync(new { error="The signed helper must be provisioned with this server's public trust key before signing." },ct);
                return;
            }
            Response.ContentType="application/octet-stream";
            Response.Headers.ContentDisposition="attachment; filename=\"Nexora.Helper.exe\"";
            await using var signed = new FileStream(HelperFile,FileMode.Open,FileAccess.Read,FileShare.Read);
            Response.ContentLength=signed.Length;
            await signed.CopyToAsync(Response.Body,ct);
            return;
        }
        var tail = TrustStamp.Tail(trust.PublicKey);
        Response.ContentType = "application/octet-stream";
        Response.Headers.ContentDisposition = "attachment; filename=\"Nexora.Helper.exe\"";
        Response.ContentLength = original + tail.Length;
        await using var fs = new FileStream(HelperFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[81920];
        long left = original;
        while (left > 0)
        {
            var n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (n == 0) break;
            await Response.Body.WriteAsync(buffer.AsMemory(0, n), ct);
            left -= n;
        }
        await Response.Body.WriteAsync(tail, ct);
    }

    /// <summary>The fingerprint of the public key helpers must carry (for checking a helper by eye; the key itself is stamped into the download).</summary>
    [HttpGet("server-key")]
    public IActionResult ServerKey() => Ok(new { fingerprint = trust.Fingerprint, publicKey = Convert.ToBase64String(trust.PublicKey) });
}

/// <summary>Graceful shutdown for nexora.ps1: loopback callers only, and only with the shared token.</summary>
[ApiController]
[AllowAnonymous]
public sealed class InternalController(IOptions<NexoraOptions> options, IHostApplicationLifetime lifetime) : ControllerBase
{
    [HttpPost("internal/shutdown")]
    public IActionResult Shutdown()
    {
        var expected = options.Value.ShutdownToken;
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (string.IsNullOrEmpty(expected) || remote is null || !System.Net.IPAddress.IsLoopback(remote))
            return NotFound();

        var supplied = Request.Headers["X-Nexora-Shutdown"].ToString();
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
        if (!ok) return NotFound();

        lifetime.StopApplication();
        return Accepted();
    }
}

/// <summary>Fleet-wide check of the SSD/HDD mapping, from what the scans stored. Admin only; everyone else gets 404.</summary>
[ApiController]
[Route("api/disk-audit")]
[Authorize(Policy = Policies.Admin)]
[HideWhenForbidden]
public sealed class DiskAuditController(IDiskAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<DiskAuditReport> Get(CancellationToken ct) => await audit.RunAsync(ct);

    /// <summary>Marks every PC's drives as due; the monitor re-reads them on its next passes and records the mapping.</summary>
    [HttpPost("rescan")]
    public async Task<IActionResult> Rescan(CancellationToken ct) =>
        this.ToAction(await audit.RescanAllAsync(HttpContext.ToActor(), ct), n => Ok(new { due = n }));

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var report = await audit.RunAsync(ct);
        var bytes = new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(DiskAudit.Csv(report.Flagged))).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"nexora-disk-audit-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }
}

/// <summary>Which of the known operator PCs has a helper connected, and as whom. The PC list is configuration (<c>Nexora:OperatorPcs</c>).</summary>
[ApiController]
[Route("api/helper-coverage")]
[Authorize(Policy = Policies.Admin)]
[HideWhenForbidden]
public sealed class HelperCoverageController(HelperDiagnostics diagnostics, IOptions<NexoraOptions> options) : ControllerBase
{
    public sealed record PcRow(string Pc, string State, string? Sid, string? Account, DateTime? LastSeenUtc, bool NameVerified, bool Configured);
    public sealed record Coverage(int Total, int Connected, int NotConnected, int NeverSeen, IReadOnlyList<PcRow> Pcs);

    [HttpGet]
    public Coverage Get() => Build(diagnostics, options.Value.OperatorPcs);

    public static Coverage Build(HelperDiagnostics diagnostics, IEnumerable<string> configured)
    {
        var names = configured.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct().ToList();
        var rows = names.Select(n => Row(n, true)).ToList();
        // a helper from a PC that is not on the list is shown too, so an unexpected one is visible
        rows.AddRange(diagnostics.AllPcs().Where(p => !names.Contains(p.Machine.ToLowerInvariant())).Select(p => Row(p.Machine, false)));
        return new Coverage(rows.Count(r => r.Configured), rows.Count(r => r.Configured && r.State == "connected"),
            rows.Count(r => r.Configured && r.State == "not connected"), rows.Count(r => r.Configured && r.State == "never seen"), rows);

        PcRow Row(string name, bool isConfigured)
        {
            var s = diagnostics.Pc(name);
            return s is null
                ? new PcRow(name, "never seen", null, null, null, false, isConfigured)
                : new PcRow(name, s.Connected ? "connected" : "not connected", s.Sid, s.Account, s.LastSeenUtc, s.NameVerified, isConfigured);
        }
    }
}
