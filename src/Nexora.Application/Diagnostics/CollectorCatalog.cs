using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Application.Diagnostics;

/// <summary>One approved collector script as shipped with the server. The hash is of the UTF-8 bytes of <see cref="Body"/>, exactly what the helper re-computes.</summary>
public sealed record CollectorScript(string Id, string Body, string Sha256);

public interface IDiagnosticCollectors
{
    IReadOnlyCollection<string> Ids { get; }
    CollectorScript? Find(string? id);
}

/// <summary>The collector scripts embedded in the server (<c>Diagnostics/Collectors/&lt;id&gt;.ps1</c>). The id is the file name.</summary>
public sealed partial class CollectorCatalog : IDiagnosticCollectors
{
    /// <summary>The helper refuses a body longer than this (DiagCollectAction.MaxBodyLength); the server never sends more.</summary>
    public const int MaxBodyLength = 20_000;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,63}\z")]
    private static partial Regex IdPattern();

    private readonly Dictionary<string, CollectorScript> _byId;

    public CollectorCatalog(IEnumerable<(string Id, string Body)> scripts)
    {
        var errors = new List<string>();
        _byId = new Dictionary<string, CollectorScript>(StringComparer.Ordinal);
        foreach (var (id, body) in scripts)
        {
            if (!IdPattern().IsMatch(id)) { errors.Add($"collector file '{id}' does not have a valid id (lowercase letters, digits, hyphen)."); continue; }
            if (string.IsNullOrWhiteSpace(body)) { errors.Add($"collector '{id}' is empty."); continue; }
            if (body.Length > MaxBodyLength) { errors.Add($"collector '{id}' is {body.Length} characters; the limit is {MaxBodyLength}."); continue; }
            if (!_byId.TryAdd(id, new CollectorScript(id, body, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))))) errors.Add($"collector id '{id}' is defined twice.");
        }
        if (errors.Count > 0) throw new DiagnosticProfileException(errors);
    }

    public IReadOnlyCollection<string> Ids => _byId.Keys;
    public CollectorScript? Find(string? id) => id is not null && _byId.TryGetValue(id, out var s) ? s : null;

    public static CollectorCatalog FromAssembly(Assembly assembly) =>
        new(EmbeddedFiles.Read(assembly, "Diagnostics/Collectors/", ".ps1").Select(f => (Path.GetFileNameWithoutExtension(f.Name), f.Text)));
}

/// <summary>What the server puts into a diag.collect job and reads back from it.</summary>
public static class DiagnosticJob
{
    public const string ActionCode = "diag.collect";
    public const int MaxExchangeHosts = 5;

    /// <summary>
    /// The signed job's <c>collectors</c> parameter: <c>[{id, sha256, body, timeoutSeconds}]</c>, the shape the helper's diag.collect action expects.
    /// Null when the profile names a collector this server does not have.
    /// </summary>
    public static string? CollectorsJson(DiagProfile profile, IDiagnosticCollectors collectors)
    {
        var list = new List<object>();
        foreach (var c in profile.Collectors)
        {
            if (collectors.Find(c.Id) is not { } script) return null;
            list.Add(new { id = script.Id, sha256 = script.Sha256, body = script.Body, timeoutSeconds = c.TimeoutSeconds });
        }
        return JsonSerializer.Serialize(list);
    }
}
