namespace Nexora.Application;

/// <summary>Read-only directory access. Implementations must never write to AD.</summary>
public interface IDirectoryLookup
{
    /// <summary>Display name for a SID, or null if it cannot be resolved.</summary>
    string? GetDisplayName(string sid);

    /// <summary>The SID of a group by name, or null if not found.</summary>
    string? ResolveGroupSid(string groupName);

    /// <summary>
    /// True when the user is a direct or nested member of the group (name or SID), per AD.
    /// Needed because distribution groups, such as "IT Service Desk", are never in a Windows token.
    /// </summary>
    bool IsUserInGroup(string userSid, string groupNameOrSid);

    DirectoryUserDto? FindUserBySid(string sid);

    IReadOnlyList<DirectoryUserDto> SearchUsers(string query, int max);

    IReadOnlyList<DirectoryComputer> SearchComputers(string? namePrefix, int max);

    /// <summary>
    /// Every AD computer's organizational unit in one paged query. A failure (bind error, timeout, access denied, no domain) comes back as
    /// <see cref="OuLookupStatus.Failed"/> with a short reason, never as an empty map.
    /// </summary>
    OuLookupResult ComputerOuMap();
}

public enum OuLookupStatus { Ok, Failed }

/// <summary>One AD computer object. <see cref="OrganizationalUnit"/> is null when the object sits directly under the domain.</summary>
public sealed record DirectoryOuEntry(string Name, string? SamAccountName, string? DnsHostName, string? OrganizationalUnit);

/// <param name="Map">Case-insensitive lookup key (AD name, sAMAccountName without '$', first label of dNSHostName) to OU.</param>
/// <param name="Error">Short, non-sensitive reason when Failed.</param>
/// <param name="Entries">The AD objects behind <paramref name="Map"/>; lets matching prefer the object whose dNSHostName equals the endpoint's FQDN.</param>
public sealed record OuLookupResult(
    OuLookupStatus Status, IReadOnlyDictionary<string, string> Map, string? Error = null, IReadOnlyList<DirectoryOuEntry>? Entries = null)
{
    public static OuLookupResult Failed(string error) =>
        new(OuLookupStatus.Failed, new Dictionary<string, string>(), error, []);

    public static OuLookupResult Ok(IReadOnlyList<DirectoryOuEntry> entries) =>
        new(OuLookupStatus.Ok, OuIndex.Create(entries).ToMap(), null, entries);

    /// <summary>Map-only result (tests, simple directories): keys are used as given.</summary>
    public static OuLookupResult Ok(IReadOnlyDictionary<string, string> map) => new(OuLookupStatus.Ok, map, null, null);
}

public enum OuMatch { NotFound, Found, FoundAmbiguous }

/// <summary>Matches endpoints to AD computer objects by name, sAMAccountName and dNSHostName (case-insensitive).</summary>
public sealed class OuIndex
{
    private readonly Dictionary<string, List<DirectoryOuEntry>> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public static string? FirstLabel(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var i = host.IndexOf('.');
        var label = (i < 0 ? host : host[..i]).Trim();
        return label.Length == 0 ? null : label;
    }

    public static OuIndex Create(IEnumerable<DirectoryOuEntry> entries)
    {
        var index = new OuIndex();
        foreach (var e in entries)
        {
            if (e.OrganizationalUnit is null) continue; // not in any OU or container: nothing to show
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(e.Name)) keys.Add(e.Name.Trim());
            if (!string.IsNullOrWhiteSpace(e.SamAccountName)) keys.Add(e.SamAccountName.Trim().TrimEnd('$'));
            if (FirstLabel(e.DnsHostName) is { } dns) keys.Add(dns);
            foreach (var k in keys)
            {
                if (!index._byKey.TryGetValue(k, out var list)) index._byKey[k] = list = [];
                list.Add(e);
            }
        }
        return index;
    }

    /// <summary>Index over a result: its entries, plus any map key the entries do not already cover.</summary>
    public static OuIndex From(OuLookupResult result)
    {
        var index = result.Entries is { Count: > 0 } ? Create(result.Entries) : new OuIndex();
        foreach (var kv in result.Map)
            if (!index._byKey.ContainsKey(kv.Key))
                index._byKey[kv.Key] = [new DirectoryOuEntry(kv.Key, null, null, kv.Value)];
        return index;
    }

    /// <summary>One OU per key; where two objects share a key the first one is kept.</summary>
    public IReadOnlyDictionary<string, string> ToMap() =>
        _byKey.ToDictionary(kv => kv.Key, kv => kv.Value[0].OrganizationalUnit!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tries Hostname, then ReportedHostname; IP addresses are never matched. When two objects share a key, the one whose dNSHostName
    /// equals the endpoint's FQDN wins, else the first (reported as <see cref="OuMatch.FoundAmbiguous"/> so the caller can log it).
    /// </summary>
    public OuMatch Resolve(string? hostname, string? reportedHostname, out string? ou)
    {
        ou = null;
        foreach (var candidate in new[] { hostname, reportedHostname })
        {
            if (string.IsNullOrWhiteSpace(candidate) || System.Net.IPAddress.TryParse(candidate.Trim(), out _)) continue;
            var label = FirstLabel(candidate);
            if (label is null || !_byKey.TryGetValue(label, out var list)) continue;
            if (list.Count > 1 && candidate.Contains('.')
                && list.FirstOrDefault(e => string.Equals(e.DnsHostName, candidate.Trim(), StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                ou = exact.OrganizationalUnit;
                return OuMatch.Found;
            }
            ou = list[0].OrganizationalUnit;
            return list.Count > 1 ? OuMatch.FoundAmbiguous : OuMatch.Found;
        }
        return OuMatch.NotFound;
    }
}
