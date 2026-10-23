using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Jobs;
using Nexora.Contracts;
using Nexora.Domain;

namespace Nexora.Application.Services;

// ---------- User profile management: the rules, the bounded read, and the list view ----------
// Reads use the agentless WMI path with the SERVER's identity and change nothing. Deleting is a catalog job (profile.delete) that runs in the
// operator's own helper; the rules here decide what the list offers and what the server refuses before a job exists.

/// <summary>Who is signed in on a PC, as read from WMI: SIDs of interactive logon sessions and account names (DOMAIN\user).</summary>
public sealed record SignedInUsers(IReadOnlyList<string> Sids, IReadOnlyList<string> Accounts);

/// <summary>Read-only "who is signed in" lookup. Throws when it cannot be determined, so callers fail safe instead of assuming nobody is.</summary>
public interface IProfileCollector
{
    Task<SignedInUsers> CollectSignedInAsync(string host, CancellationToken ct);
}

/// <param name="SignedIn">Null when who is signed in could not be determined (profiles are then never deletable).</param>
public sealed record ProfileSnapshot(IReadOnlyList<ProfileInfo> Profiles, SignedInUsers? SignedIn, DateTime AtUtc);

public sealed record ProfileRowDto(
    string Sid, string? Account, string LocalPath, bool Loaded, long? SizeBytes, DateTime? SizeMeasuredUtc,
    DateTime? LastUsedUtc, string? LastUsedSource, bool Deletable, string? BlockReason, string? State = null);

/// <summary>The list envelope. <see cref="Unavailable"/> rows are the last known ones and never deletable.</summary>
public sealed record ProfileListOutcome(bool Unavailable, string? Reason, IReadOnlyList<ProfileRowDto> Items, DateTime? LastKnownUtc = null);

public static partial class ProfileRules
{
    public const string SignedInUnknown = "could not verify who is signed in";
    public const string SignedInNow = "The user is signed in on that PC right now.";

    [GeneratedRegex(@"^S-1-5-21-\d+-\d+-\d+-\d+\z")]
    private static partial Regex DomainOrLocalUserSid();

    [GeneratedRegex(@"^S-1-12-1(-\d+){4}\z")]
    private static partial Regex AzureAdUserSid();
    /// <summary>X:\Users\name with a plain ASCII name: letters, digits, dot, underscore, space, hyphen; no leading or trailing dot or space; so no 8.3 short names (~) or %variables%.</summary>
    [GeneratedRegex(@"^[A-Za-z]:\\Users\\(?=[A-Za-z0-9_-])(?!.*[. ]\z)[A-Za-z0-9._ -]{1,64}\z")]
    private static partial Regex DeletablePath();

    [GeneratedRegex(@"^[A-Za-z]:\\Users\\[^\\]+\z")]
    private static partial Regex DirectlyUnderUsers();

    /// <summary>The folder as Windows compares it: case-insensitive, trailing separators, dots and spaces ignored.</summary>
    public static string NormalizedPath(string? path) => (path ?? "").Trim().TrimEnd('\\', '.', ' ').ToUpperInvariant();

    [GeneratedRegex(@"^[A-Za-z]:\\Windows\\", RegexOptions.IgnoreCase)]
    private static partial Regex UnderWindows();

    /// <summary>Folder names that are never an ordinary user's profile.</summary>
    public static readonly IReadOnlyList<string> SystemFolders = ProtectedNames.SystemProfileFolders;

    /// <summary>Real people only: a domain/local account SID (S-1-5-21-a-b-c-rid) or an Azure AD one (S-1-12-1-a-b-c-d). Pattern is anchored with \z.</summary>
    public static bool IsUserSid(string? sid) => sid is not null && (DomainOrLocalUserSid().IsMatch(sid) || AzureAdUserSid().IsMatch(sid));

    public static string Leaf(string? path) => (path ?? "").TrimEnd('\\').Split('\\').Last();

    /// <summary>The folder name as Windows sees it: trailing dots and spaces are ignored, so "Public ." is Public.</summary>
    public static string NormalizedLeaf(string? path) => Leaf(path).TrimEnd('.', ' ');

    /// <summary>System, service, default and public profiles. These are filtered out of the list and can never be deleted.</summary>
    public static bool IsSystemProfile(ProfileInfo p) =>
        p.Special || !IsUserSid(p.Sid) || SystemFolders.Contains(NormalizedLeaf(p.LocalPath), StringComparer.OrdinalIgnoreCase) || UnderWindows().IsMatch(p.LocalPath ?? "")
        || new[] { "SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE" }.Contains(p.Account?.Split('\\').Last(), StringComparer.OrdinalIgnoreCase)
        || (DomainOrLocalUserSid().IsMatch(p.Sid) && new[] { "501", "503", "504" }.Contains(p.Sid.Split('-').Last(), StringComparer.Ordinal));

    /// <summary>The built-in Administrator (RID 500) profile: hidden and never deletable from Nexora.</summary>
    public static bool IsBuiltInAdministrator(ProfileInfo p) =>
        p.Sid.EndsWith("-500", StringComparison.Ordinal) || string.Equals(NormalizedLeaf(p.LocalPath), "Administrator", StringComparison.OrdinalIgnoreCase);

    public static bool IsSignedIn(ProfileInfo p, SignedInUsers users) =>
        users.Sids.Contains(p.Sid, StringComparer.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(p.Account) && users.Accounts.Contains(p.Account, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Why this profile must not be deleted, or null when it may. The single rule used by the list (deletable / blockReason), the batch confirmation
    /// and the single-job verification, so the three can never disagree. Checks run most-important first; the role check is last.
    /// </summary>
    /// <param name="signedIn">Null = could not be determined, which refuses (fail safe).</param>
    /// <param name="initiatorSid">The operator asking; their own profile is refused.</param>
    /// <param name="protectedReason">Why the workstation is protected, or null.</param>
    /// <param name="isAdmin">False adds a role refusal (the list shows it to Operators; the server enforces the role again on every request).</param>
    public static string? BlockReason(ProfileInfo p, SignedInUsers? signedIn, string? initiatorSid, string? protectedReason, bool isAdmin, IReadOnlyList<ProfileInfo>? all = null)
    {
        if (IsSystemProfile(p)) return "System profiles cannot be deleted.";
        if (p.Loaded) return SignedInNow;
        if (signedIn is null) return SignedInUnknown;
        if (IsSignedIn(p, signedIn)) return SignedInNow;
        if (initiatorSid is not null && string.Equals(p.Sid, initiatorSid, StringComparison.OrdinalIgnoreCase)) return "This is your own profile.";
        if (IsBuiltInAdministrator(p)) return "The built-in Administrator profile cannot be deleted.";
        if (string.IsNullOrWhiteSpace(p.Account)) return "The account could not be verified (the SID does not resolve to a name), so the profile is not deletable.";
        if (!DeletablePath().IsMatch(p.LocalPath ?? ""))
            return DirectlyUnderUsers().IsMatch(p.LocalPath ?? "") ? "profile folder name not supported" : @"The profile folder is not directly under X:\Users.";
        // Two profiles pointing at one folder (a copied or hand-edited registration) could delete someone else's data.
        if (all is not null && all.Any(o => !string.Equals(o.Sid, p.Sid, StringComparison.OrdinalIgnoreCase) && NormalizedPath(o.LocalPath) == NormalizedPath(p.LocalPath)))
            return "Another profile on this PC uses the same folder, so it is not deletable.";
        if (protectedReason is not null) return $"Protected workstation ({protectedReason}).";
        if (!isAdmin) return "Deleting a profile needs the Admin role.";
        return null;
    }
}

/// <summary>Where "last used" comes from. FILETIME decoding is plain integer arithmetic, so it does not depend on the machine's culture.</summary>
public static class ProfileLastUsed
{
    public const string ProfileList = "ProfileList", Wmi = "Wmi";

    /// <summary>A FILETIME stored as two DWORDs (the ProfileList LocalProfileLoadTimeHigh/Low values). Zero or unrepresentable values give null.</summary>
    public static DateTime? FromFileTime(uint high, uint low)
    {
        var ticks = ((long)high << 32) | low;
        if (ticks <= 0) return null;
        try
        {
            var utc = DateTime.FromFileTimeUtc(ticks);
            return utc.Year < 1995 ? null : utc;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// ProfileList (the newer of load and unload time) beats Win32_UserProfile.LastUseTime (that one is often refreshed by services
    /// and scans and cannot be trusted alone). No file is read on the endpoint to get a date.
    /// </summary>
    public static (DateTime? Utc, string? Source) Choose(DateTime? loadUtc, DateTime? unloadUtc, DateTime? wmiUtc)
    {
        DateTime? profileList = loadUtc is { } l && unloadUtc is { } u ? (l >= u ? l : u) : loadUtc ?? unloadUtc;
        if (profileList is { } p) return (p, ProfileList);
        return wmiUtc is { } w ? (w, Wmi) : (null, null);
    }
}

// ---------- the bounded, cached reader ----------

public sealed record ProfileRead(ProfileSnapshot? Value, string? Reason)
{
    public bool Ok => Value is not null;
}

public interface IProfileReader
{
    /// <summary>Reads the profiles and who is signed in. <paramref name="maxAge"/> of zero or less always reads again (used before a deletion is accepted).</summary>
    Task<ProfileRead> ReadAsync(string host, TimeSpan maxAge, CancellationToken ct);
    /// <summary>The snapshot of the last successful read of this host in this server process (for showing an offline workstation), or null.</summary>
    ProfileSnapshot? LastKnown(string host);
}

/// <summary>
/// The only way the server reads profiles. Bounded like <see cref="ServiceReader"/>: at most <see cref="MaxConcurrent"/> reads at once (a slot is
/// held until the WMI work really ends), a hard deadline per read, a short cache, and a failed host is remembered briefly. A failure is a typed
/// result with a generic reason, never an exception. Singleton.
/// </summary>
public sealed class ProfileReader(IEndpointCollector profiles, IProfileCollector signedIn, IOptions<NexoraOptions> options, ISystemClock clock, ILogger<ProfileReader> logger) : IProfileReader
{
    public const int MaxConcurrent = 4, MaxCacheEntries = 64;
    public const string ReadFailed = "The profiles could not be read from the workstation right now.";
    public const string Busy = "Too many profile reads are running. Try again in a moment.";
    private static readonly TimeSpan FailureMemory = TimeSpan.FromSeconds(5);

    private sealed record Entry(DateTime AtUtc, ProfileSnapshot? Value, string? Reason);

    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProfileSnapshot> _lastGood = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<ProfileRead>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public ProfileSnapshot? LastKnown(string host)
    {
        lock (_lock) return _lastGood.GetValueOrDefault(host);
    }

    public async Task<ProfileRead> ReadAsync(string host, TimeSpan maxAge, CancellationToken ct)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            // A fresh read is never merged with one that started earlier: it must show the PC as it is now.
            var own = await FetchAsync(host).WaitAsync(ct);
            return own;
        }

        Task<ProfileRead> task;
        TaskCompletionSource<ProfileRead>? mine = null;
        lock (_lock)
        {
            if (_cache.TryGetValue(host, out var hit))
            {
                var age = clock.UtcNow - hit.AtUtc;
                if (hit.Reason is null ? age <= maxAge : age <= FailureMemory) return new ProfileRead(hit.Value, hit.Reason);
            }
            if (_inflight.TryGetValue(host, out var running)) task = running;
            else
            {
                mine = new TaskCompletionSource<ProfileRead>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = mine.Task;
                _inflight[host] = task;
            }
        }
        if (mine is not null) _ = RunSharedAsync(host, mine);   // detached: the caller that started it can leave without cancelling the shared read
        return await task.WaitAsync(ct);
    }

    private async Task RunSharedAsync(string host, TaskCompletionSource<ProfileRead> done)
    {
        try { done.SetResult(await FetchAsync(host)); }
        catch (Exception ex) { done.SetException(ex); }   // FetchAsync reports failures as results; this is only a safety net
        finally { lock (_lock) _inflight.Remove(host); }
    }

    private async Task<ProfileRead> FetchAsync(string host)
    {
        var seconds = Math.Clamp(options.Value.Monitoring.ProfileReadTimeoutSeconds, 2, 180);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var holdingSlot = false;
        try
        {
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(Math.Min(5, seconds)), deadline.Token)) return new(null, Busy);
            holdingSlot = true;
            var work = ReadAllAsync(host, deadline.Token);
            holdingSlot = false;
            // The slot is freed when the WMI work really ends, not when this read stops waiting; the failure of an abandoned call is observed here.
            _ = work.ContinueWith(t => { _ = t.Exception; _gate.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var snapshot = await work.WaitAsync(deadline.Token);
            Remember(host, new Entry(clock.UtcNow, snapshot, null), snapshot);
            return new(snapshot, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Profile read of {Host} failed or timed out", host);
            Remember(host, new Entry(clock.UtcNow, null, ReadFailed), null);
            return new(null, ReadFailed);
        }
        finally
        {
            if (holdingSlot) _gate.Release();   // ReadAllAsync threw before it returned a task
        }
    }

    private async Task<ProfileSnapshot> ReadAllAsync(string host, CancellationToken ct)
    {
        var list = await profiles.CollectProfilesAsync(host, ct);
        SignedInUsers? users = null;
        try { users = await signedIn.CollectSignedInAsync(host, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail safe: without knowing who is signed in nothing is offered for deletion.
            logger.LogWarning(ex, "Could not determine who is signed in on {Host}", host);
        }
        return new ProfileSnapshot(list, users, clock.UtcNow);
    }

    private void Remember(string host, Entry entry, ProfileSnapshot? good)
    {
        lock (_lock)
        {
            _cache[host] = entry;
            if (good is not null) _lastGood[host] = good;
            Trim(_cache.Count - MaxCacheEntries, _cache, kv => kv.Value.AtUtc);
            Trim(_lastGood.Count - MaxCacheEntries, _lastGood, kv => kv.Value.AtUtc);
        }
    }

    private static void Trim<T>(int excess, Dictionary<string, T> map, Func<KeyValuePair<string, T>, DateTime> at)
    {
        if (excess <= 0) return;
        foreach (var k in map.OrderBy(at).Take(excess).Select(kv => kv.Key).ToList()) map.Remove(k);
    }
}

// ---------- the list view ----------

public interface IProfileQuery
{
    Task<Result<ProfileListOutcome>> ListAsync(JobActor actor, Guid endpointId, CancellationToken ct = default);
}

public sealed class ProfileQuery(INexoraDb db, ISettingsService settings, IProfileReader reader) : IProfileQuery
{
    public const string Offline = "The workstation is offline.";
    private static readonly TimeSpan ListMaxAge = TimeSpan.FromSeconds(5);

    public async Task<Result<ProfileListOutcome>> ListAsync(JobActor actor, Guid endpointId, CancellationToken ct = default)
    {
        var e = await db.Endpoints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == endpointId, ct);
        if (e is null) return Result<ProfileListOutcome>.Fail(ErrorKind.NotFound, "Endpoint not found.");
        var sizes = (await db.ProfileSizes.AsNoTracking().Where(s => s.EndpointId == endpointId).ToListAsync(ct))
            .ToDictionary(s => s.Sid, StringComparer.OrdinalIgnoreCase);

        string reason;
        if (!e.Online) reason = Offline;
        else
        {
            var read = await reader.ReadAsync(e.Hostname, ListMaxAge, ct);
            if (read.Ok)
            {
                var protectedReason = JobService.ProtectedReason(e, await settings.GetAsync(ct));
                return Result<ProfileListOutcome>.Success(new ProfileListOutcome(false, null, Rows(read.Value!, sizes, actor, protectedReason, live: true)));
            }
            reason = read.Reason ?? ProfileReader.ReadFailed;
        }

        // Unavailable: show what was seen last, with every deletion switched off.
        return Result<ProfileListOutcome>.Success(reader.LastKnown(e.Hostname) is { } known
            ? new ProfileListOutcome(true, reason, Rows(known, sizes, actor, null, live: false), known.AtUtc)
            : new ProfileListOutcome(true, reason, []));
    }

    private static List<ProfileRowDto> Rows(ProfileSnapshot snapshot, Dictionary<string, ProfileSize> sizes, JobActor actor, string? protectedReason, bool live) =>
        snapshot.Profiles.Where(p => !ProfileRules.IsSystemProfile(p) && !ProfileRules.IsBuiltInAdministrator(p))
            .OrderByDescending(p => p.LastUseUtc ?? DateTime.MinValue).ThenBy(p => p.Account ?? p.Sid, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var block = live
                    ? ProfileRules.BlockReason(p, snapshot.SignedIn, actor.Sid, protectedReason, actor.IsAdmin, snapshot.Profiles)
                    : "Last known data: the workstation cannot be read right now.";
                sizes.TryGetValue(p.Sid, out var size);
                return new ProfileRowDto(p.Sid, string.IsNullOrWhiteSpace(p.Account) ? null : p.Account, p.LocalPath, p.Loaded,
                    size?.SizeBytes, size?.MeasuredUtc, p.LastUseUtc, p.LastUseUtc is null ? null : p.LastUseSource, block is null, block, p.Status);
            }).ToList();
}

/// <summary>Reads the helper's profile.sizes output (one JSON object per line) into measurements. Pure; the caller decides what to store.</summary>
public static class ProfileSizeOutput
{
    public const int MaxRows = 500;

    public sealed record Measurement(string Sid, long Bytes);

    /// <summary>
    /// Lines that are not a well-formed measurement are ignored. A partial measurement (the folder could not be read in full) is skipped: a
    /// figure that understates the data must not look like the size. When <paramref name="onlySid"/> is set, other SIDs are ignored.
    /// </summary>
    public static IReadOnlyList<Measurement> Parse(IEnumerable<string> lines, string? onlySid)
    {
        var found = new Dictionary<string, Measurement>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (found.Count >= MaxRows) break;
            if (line.Length < 10 || line[0] != '{') continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!root.TryGetProperty("sid", out var sidEl) || sidEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var sid = sidEl.GetString() ?? "";
                if (!ProfileRules.IsUserSid(sid)) continue;
                if (onlySid is not null && !string.Equals(sid, onlySid, StringComparison.OrdinalIgnoreCase)) continue;
                if (root.TryGetProperty("partial", out var part) && part.ValueKind == System.Text.Json.JsonValueKind.True) continue;
                if (!root.TryGetProperty("bytes", out var bytesEl) || bytesEl.ValueKind != System.Text.Json.JsonValueKind.Number || !bytesEl.TryGetInt64(out var bytes) || bytes < 0) continue;
                found[sid] = new Measurement(sid, bytes);
            }
            catch (System.Text.Json.JsonException) { /* not a measurement line */ }
        }
        return found.Values.ToList();
    }
}
