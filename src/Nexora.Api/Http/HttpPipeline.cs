using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Nexora.Application;

namespace Nexora.Api.Http;

public sealed partial class SecurityHeadersMiddleware(RequestDelegate next)
{
    [GeneratedRegex(@"^[A-Za-z0-9.\-]{1,255}(:\d{1,5})?$")]
    private static partial Regex SafeHost();

    public Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Value;
        // Allow the SignalR WebSocket back to this same host (older browsers do not treat ws: as 'self' on http).
        var ws = SafeHost().IsMatch(host ?? "") ? $" {(context.Request.IsHttps ? "wss" : "ws")}://{host}" : "";

        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; style-src-attr 'unsafe-inline'; " +
            $"img-src 'self' data:; connect-src 'self'{ws}; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        return next(context);
    }
}

/// <summary>
/// CSRF defence for cookie-less Windows auth (browsers attach Kerberos/NTLM credentials automatically, so a
/// cross-site request is authenticated). Two layers on every state-changing request:
/// 1. An Origin header, when present, must match the Host (browsers always send it cross-site).
/// 2. Anti-forgery token validation for /api and the browser hub.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next, IAntiforgery antiforgery, ILogger<CsrfMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var unsafeMethod = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                             || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method));
        var isWebSocket = context.WebSockets.IsWebSocketRequest;

        if ((unsafeMethod || isWebSocket) && IsGuardedPath(request.Path))
        {
            if (!OriginMatches(request))
            {
                logger.LogWarning("Blocked cross-origin {Method} {Path} from origin {Origin}",
                    request.Method, request.Path, request.Headers.Origin.ToString());
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // Token check only for authenticated callers: anonymous ones get the normal 401 challenge instead.
            if (unsafeMethod && NeedsToken(request.Path) && context.User.Identity?.IsAuthenticated == true)
            {
                try { await antiforgery.ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException ex)
                {
                    logger.LogWarning(ex, "Anti-forgery validation failed for {Method} {Path}", request.Method, request.Path);
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid anti-forgery token." });
                    return;
                }
            }
        }
        await next(context);
    }

    private static bool IsGuardedPath(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");

    // The helper hub is a non-browser client; it is protected by the Origin check and its Windows identity.
    private static bool NeedsToken(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs/nexora");

    internal static bool OriginMatches(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // non-browser client
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var expected)
               && string.Equals(uri.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(uri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
               && uri.Port == expected.Port
               && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/"
               && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }
}

/// <summary>
/// Records every attempt of a helper to open its channel (negotiate and WebSocket upgrade), accepted or refused, with who and from where,
/// so "the helper does not connect" can be diagnosed from the server. A request that never reaches the server (wrong port, no network) cannot appear here.
/// </summary>
public sealed class HelperAttemptMiddleware(RequestDelegate next, Nexora.Api.Services.HelperDiagnostics diagnostics, ILogger<HelperAttemptMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var isAttempt = path.StartsWithSegments("/hubs/helper")
                        && (path.Value!.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) || context.WebSockets.IsWebSocketRequest);
        await next(context);
        if (!isAttempt) return;

        var user = context.User;
        // An unauthenticated 401 is the normal first leg of Windows sign-in (the browser or helper then answers the challenge); it says nothing about this attempt.
        if (context.Response.StatusCode == 401 && user.Identity?.IsAuthenticated != true)
        {
            var from = context.Connection.RemoteIpAddress?.ToString();
            logger.LogDebug("Helper channel challenged {Ip} for Windows sign-in", from);
            if (diagnostics.Challenge(from, DateTime.UtcNow))
                logger.LogWarning("A helper at {Ip} keeps failing Windows sign-in: {Count} challenges without an answer. Check that the helper runs as a domain user and that {Url} is in the Local intranet zone / allowed for integrated sign-in.",
                    from, diagnostics.Unanswered401s(from!), $"{context.Request.Scheme}://{context.Request.Host}");
            return;
        }
        if (user.Identity?.IsAuthenticated == true) diagnostics.ChallengesAnswered(context.Connection.RemoteIpAddress?.ToString());
        var sid = user.Identity?.IsAuthenticated == true ? Nexora.Api.Auth.PrincipalExtensions.GetSid(user) : null;
        var role = new[] { Nexora.Domain.NexoraRoles.SuperAdmin, Nexora.Domain.NexoraRoles.Admin, Nexora.Domain.NexoraRoles.Operator, Nexora.Domain.NexoraRoles.Viewer }.FirstOrDefault(user.IsInRole);
        var crossOrigin = !CsrfMiddleware.OriginMatches(context.Request);
        var (outcome, reason) = Nexora.Api.Services.HelperDiagnostics.Decide(context.Response.StatusCode, user.Identity?.IsAuthenticated == true, role, crossOrigin);
        var ip = context.Connection.RemoteIpAddress?.ToString();

        diagnostics.Attempt(sid, new(DateTime.UtcNow, outcome, reason, ip, path.Value ?? ""));
        var account = user.Identity?.IsAuthenticated == true ? user.Identity.Name : "anonymous";
        if (outcome == "accepted") logger.LogInformation("Helper connection attempt accepted for {Account} ({Sid}) from {Ip}", account, sid, ip);
        else logger.LogWarning("Helper connection attempt refused for {Account} ({Sid}) from {Ip}: {Reason} (HTTP {Status})", account, sid, ip, reason, context.Response.StatusCode);
    }
}

public static class ResultExtensions
{
    public static IActionResult ToAction(this ControllerBase c, Result result, Func<IActionResult>? ok = null) =>
        result.Ok ? ok?.Invoke() ?? c.NoContent() : Error(c, result.Kind, result.Message);

    public static IActionResult ToAction<T>(this ControllerBase c, Result<T> result, Func<T, IActionResult>? ok = null) =>
        result.Ok ? ok?.Invoke(result.Value!) ?? c.Ok(result.Value) : Error(c, result.Kind, result.Message);

    private static ObjectResult Error(ControllerBase c, ErrorKind kind, string? message)
    {
        var status = kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            ErrorKind.NotImplemented => StatusCodes.Status501NotImplemented,
            _ => StatusCodes.Status500InternalServerError,
        };
        return new ObjectResult(new { error = message }) { StatusCode = status };
    }
}

/// <summary>Streams CSV (UTF-8 with BOM) as an attachment, writing rows as they are produced.</summary>
public sealed class CsvStreamResult(string fileName, Func<TextWriter, CancellationToken, Task> write) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = "text/csv; charset=utf-8";
        // Quoted ASCII fallback plus RFC 5987 form; the name is generated by the server, never from user input.
        response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        response.Headers.CacheControl = "no-store";
        await using var writer = new StreamWriter(response.Body, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 16 * 1024, leaveOpen: true);
        await write(writer, context.HttpContext.RequestAborted);
        await writer.FlushAsync(context.HttpContext.RequestAborted);
    }
}
