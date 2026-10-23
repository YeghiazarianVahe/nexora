using System.Collections.Concurrent;
using Nexora.Api.Auth;
using Nexora.Domain;

namespace Nexora.Api.Services;

/// <summary>One attempt of a helper to open its channel: when, from where, and what the server decided and why.</summary>
public sealed record HelperAttempt(DateTime AtUtc, string Outcome, string Reason, string? RemoteIp, string Path);

public sealed record HelperSnapshot(bool EverConnected, DateTime? LastConnectedUtc, DateTime? LastDisconnectedUtc, HelperAttempt? LastAttempt);

/// <summary>
/// What the server knows about each operator's helper since it started: attempts (accepted or refused, with the reason), and when it was last
/// connected or lost. The banner and the top bar use it to tell "never connected" from "was connected, now gone".
/// </summary>
public sealed class HelperDiagnostics
{
    private sealed class Entry
    {
        public DateTime? Connected, Disconnected;
        public HelperAttempt? Last;
    }

    private readonly ConcurrentDictionary<string, Entry> _by = new(StringComparer.OrdinalIgnoreCase);

    public void Attempt(string? sid, HelperAttempt attempt)
    {
        if (sid is null) return;
        _by.GetOrAdd(sid, _ => new()).Last = attempt;
    }

    // ---- sign-in challenges that never get answered (a helper whose Windows sign-in fails leaves only these)
    private sealed class Unanswered { public int Count; public DateTime LastWarned; }
    private readonly ConcurrentDictionary<string, Unanswered> _unanswered = new();
    public const int ChallengeWarnAfter = 6;

    /// <summary>
    /// One unauthenticated 401 on the helper channel from this address. A normal sign-in has one or two before it succeeds; many in a row with no
    /// success mean the helper cannot complete Windows sign-in. Returns true when that should be reported (at most once every 10 minutes per address).
    /// </summary>
    public bool Challenge(string? ip, DateTime nowUtc)
    {
        if (ip is null) return false;
        var u = _unanswered.GetOrAdd(ip, _ => new());
        lock (u)
        {
            u.Count++;
            if (u.Count < ChallengeWarnAfter || nowUtc - u.LastWarned < TimeSpan.FromMinutes(10)) return false;
            u.LastWarned = nowUtc;
            return true;
        }
    }

    public int Unanswered401s(string ip) => _unanswered.TryGetValue(ip, out var u) ? u.Count : 0;

    /// <summary>The address completed a sign-in: its unanswered challenges no longer count.</summary>
    public void ChallengesAnswered(string? ip)
    {
        if (ip is not null) _unanswered.TryRemove(ip, out _);
    }

    // ---- per PC (the helper reports its machine name when it connects)
    public sealed record PcState(string Machine, string Sid, string? Account, bool Connected, DateTime? ConnectedUtc, DateTime? LastSeenUtc, bool NameVerified, string? RemoteIp);

    private readonly ConcurrentDictionary<string, PcState> _pcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pcByConnection = new();

    /// <summary>Machine names are self-reported by the helper; keep only a plain host name.</summary>
    public static string? CleanMachine(string? raw)
    {
        var m = (raw ?? "").Trim();
        if (m.Length is 0 or > 63) return null;
        return m.All(c => char.IsLetterOrDigit(c) || c is '-' or '_') ? m.ToLowerInvariant() : null;
    }

    public void PcConnected(string connectionId, string machine, string sid, string? account, bool verified, string? ip)
    {
        _pcByConnection[connectionId] = machine;
        var now = DateTime.UtcNow;
        _pcs[machine] = new PcState(machine, sid, account, true, now, now, verified, ip);
    }

    public void PcDisconnected(string connectionId)
    {
        if (!_pcByConnection.TryRemove(connectionId, out var machine) || !_pcs.TryGetValue(machine, out var s)) return;
        // another connection of the same PC may still be up
        if (_pcByConnection.Values.Contains(machine, StringComparer.OrdinalIgnoreCase)) return;
        _pcs[machine] = s with { Connected = false, LastSeenUtc = DateTime.UtcNow };
    }

    public PcState? Pc(string machine) => _pcs.TryGetValue(machine, out var s) ? s : null;
    public IReadOnlyCollection<PcState> AllPcs() => _pcs.Values.ToList();

    public void Connected(string sid) => _by.GetOrAdd(sid, _ => new()).Connected = DateTime.UtcNow;
    public void Disconnected(string sid) => _by.GetOrAdd(sid, _ => new()).Disconnected = DateTime.UtcNow;

    public HelperSnapshot Snapshot(string sid) =>
        _by.TryGetValue(sid, out var e) ? new(e.Connected is not null, e.Connected, e.Disconnected, e.Last) : new(false, null, null, null);

    /// <summary>The words for a refused attempt, from the HTTP status and what the server knows about the caller.</summary>
    public static (string Outcome, string Reason) Decide(int status, bool authenticated, string? role, bool crossOrigin) => status switch
    {
        200 or 101 => ("accepted", "ok"),
        401 => ("rejected", "Windows sign-in was not completed (the credentials were missing or refused)"),
        403 when crossOrigin => ("rejected", "cross-origin request blocked"),
        403 when !authenticated => ("rejected", "not signed in"),
        403 when role is null => ("rejected", "the account has no Nexora role (not approved yet, or revoked)"),
        403 => ("rejected", $"the {role} role is below Operator"),
        _ => ("rejected", $"HTTP {status}"),
    };
}
