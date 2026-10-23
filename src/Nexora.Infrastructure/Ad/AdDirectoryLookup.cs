using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Nexora.Application;

namespace Nexora.Infrastructure.Ad;

/// <summary>Read-only AD lookup. No writes, no stored credentials; queries run as the host process.</summary>
public sealed class AdDirectoryLookup(ILogger<AdDirectoryLookup> logger) : IDirectoryLookup
{
    public string? GetDisplayName(string sid) => FindUserBySid(sid)?.DisplayName;

    public string? ResolveGroupSid(string groupName)
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Domain);
            using var group = GroupPrincipal.FindByIdentity(context, IdentityType.Name, groupName)
                              ?? GroupPrincipal.FindByIdentity(context, IdentityType.SamAccountName, groupName);
            return group?.Sid?.Value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve group {Group}", groupName);
            return null;
        }
    }

    public bool IsUserInGroup(string userSid, string groupNameOrSid)
    {
        try
        {
            using var root = new DirectoryEntry();
            var groupFilter = groupNameOrSid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? $"(objectSid={EscapeFilter(groupNameOrSid)})"
                : $"(|(cn={EscapeFilter(groupNameOrSid)})(sAMAccountName={EscapeFilter(groupNameOrSid)}))";
            using var groupSearch = new DirectorySearcher(root, $"(&(objectCategory=group){groupFilter})", ["distinguishedName"], SearchScope.Subtree);
            var group = groupSearch.FindOne();
            if (group is null || group.Properties["distinguishedName"].Count == 0) return false;
            var groupDn = group.Properties["distinguishedName"][0]!.ToString()!;

            // LDAP_MATCHING_RULE_IN_CHAIN: expands nested groups, and works for distribution groups.
            var filter = $"(&(objectCategory=person)(objectSid={EscapeFilter(userSid)})(memberOf:1.2.840.113556.1.4.1941:={EscapeFilter(groupDn)}))";
            using var memberSearch = new DirectorySearcher(root, filter, ["objectSid"], SearchScope.Subtree);
            return memberSearch.FindOne() is not null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Group membership lookup failed for {Sid} in {Group}", userSid, groupNameOrSid);
            return false;
        }
    }

    public DirectoryUserDto? FindUserBySid(string sid)
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Domain);
            using var user = UserPrincipal.FindByIdentity(context, IdentityType.Sid, sid);
            return user is null ? null : ToDto(user, sid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AD lookup failed for {Sid}", sid);
            return null;
        }
    }

    public IReadOnlyList<DirectoryUserDto> SearchUsers(string query, int max)
    {
        var result = new Dictionary<string, DirectoryUserDto>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var context = new PrincipalContext(ContextType.Domain);
            foreach (var template in new[]
            {
                new UserPrincipal(context) { DisplayName = $"*{Escape(query)}*", Enabled = true },
                new UserPrincipal(context) { SamAccountName = $"{Escape(query)}*", Enabled = true },
            })
            {
                using (template)
                using (var searcher = new PrincipalSearcher(template))
                {
                    foreach (var p in searcher.FindAll().OfType<UserPrincipal>().Take(max))
                    {
                        using (p)
                        {
                            if (p.Sid is null) continue;
                            result.TryAdd(p.Sid.Value, ToDto(p, p.Sid.Value));
                        }
                        if (result.Count >= max) break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AD user search failed");
        }
        return result.Values.OrderBy(u => u.DisplayName).Take(max).ToList();
    }

    public IReadOnlyList<DirectoryComputer> SearchComputers(string? namePrefix, int max)
    {
        var prefix = string.IsNullOrWhiteSpace(namePrefix) ? "" : EscapeFilter(namePrefix.Trim());
        var filter = "(&(objectCategory=computer)(operatingSystem=Windows*)(!(operatingSystem=*Server*))" +
                     "(!(userAccountControl:1.2.840.113556.1.4.803:=2))" + $"(name={prefix}*))";
        using var searcher = new DirectorySearcher(new DirectoryEntry(), filter,
            ["name", "operatingSystem", "distinguishedName"], SearchScope.Subtree)
        { PageSize = 500, SizeLimit = max };
        using var hits = searcher.FindAll();
        var list = new List<DirectoryComputer>();
        foreach (SearchResult hit in hits)
        {
            var name = hit.Properties["name"].Count > 0 ? hit.Properties["name"][0]?.ToString() : null;
            if (name is null) continue;
            var os = hit.Properties["operatingSystem"].Count > 0 ? hit.Properties["operatingSystem"][0]?.ToString() : null;
            var dn = hit.Properties["distinguishedName"].Count > 0 ? hit.Properties["distinguishedName"][0]?.ToString() : null;
            list.Add(new DirectoryComputer(name, os, AdPaths.OuFromDn(dn)));
        }
        return list;
    }

    public OuLookupResult ComputerOuMap()
    {
        try
        {
            var entries = new List<DirectoryOuEntry>();
            using var root = new DirectoryEntry();
            using var searcher = new DirectorySearcher(root, "(objectCategory=computer)",
                ["name", "sAMAccountName", "dNSHostName", "distinguishedName"], SearchScope.Subtree)
            { PageSize = 1000, ServerTimeLimit = TimeSpan.FromSeconds(60), ClientTimeout = TimeSpan.FromSeconds(90) };
            using var hits = searcher.FindAll();
            foreach (SearchResult hit in hits)
            {
                var name = First(hit, "name");
                if (name is null) continue;
                entries.Add(new DirectoryOuEntry(name, First(hit, "sAMAccountName"), First(hit, "dNSHostName"), AdPaths.OuFromDn(First(hit, "distinguishedName"))));
            }
            return OuLookupResult.Ok(entries);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read computer OUs from AD");
            return OuLookupResult.Failed(ex switch
            {
                UnauthorizedAccessException => "Access to Active Directory was denied.",
                System.Runtime.InteropServices.COMException => "Active Directory could not be reached.",
                TimeoutException or OperationCanceledException => "The Active Directory query timed out.",
                _ => "The Active Directory query failed.",
            });
        }
    }

    private static string? First(SearchResult hit, string property) =>
        hit.Properties[property].Count > 0 ? hit.Properties[property][0]?.ToString() : null;

    private static DirectoryUserDto ToDto(UserPrincipal user, string sid)
    {
        string account;
        try { account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch { account = user.SamAccountName ?? sid; }
        var display = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Name ?? account : user.DisplayName;
        return new DirectoryUserDto(sid, account, display);
    }

    /// <summary>PrincipalSearcher wildcard filters: strip characters that would widen or break the query.</summary>
    private static string Escape(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_').ToArray());

    /// <summary>RFC 4515 escaping for a raw LDAP filter value.</summary>
    private static string EscapeFilter(string s) =>
        s.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
}
