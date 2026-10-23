using System.Text;

namespace Nexora.Application;

public static class AdPaths
{
    /// <summary>
    /// "CN=PC-1,OU=Workstations,OU=Branch A,DC=corp,DC=local" becomes "Branch A/Workstations" (root first, no domain, no computer).
    /// A computer in a container such as CN=Computers gives "Computers".
    /// </summary>
    public static string? OuFromDn(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        var parts = SplitDn(dn);
        if (parts.Count < 2) return null;
        var containers = parts.Skip(1)
            .Where(p => p.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..])
            .Reverse().ToList();
        return containers.Count == 0 ? null : string.Join("/", containers);
    }

    /// <summary>
    /// Splits on commas that are not escaped, and unescapes the parts: backslash-character escapes and backslash-hex escapes such as \2C
    /// (UTF-8 byte sequences). Unescaped blanks at the start of a part are skipped; blanks inside a value are kept.
    /// </summary>
    private static List<string> SplitDn(string dn)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        var bytes = new List<byte>();
        void Flush()
        {
            if (bytes.Count == 0) return;
            cur.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
        for (var i = 0; i < dn.Length; i++)
        {
            var c = dn[i];
            if (c == '\\' && i + 2 < dn.Length && Uri.IsHexDigit(dn[i + 1]) && Uri.IsHexDigit(dn[i + 2]))
            {
                bytes.Add(Convert.ToByte(dn.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            Flush();
            if (c == '\\' && i + 1 < dn.Length) { cur.Append(dn[i + 1]); i++; }
            else if (c == ',') { parts.Add(cur.ToString()); cur.Clear(); }
            else if (c == ' ' && cur.Length == 0) { /* blank after the separating comma */ }
            else cur.Append(c);
        }
        Flush();
        parts.Add(cur.ToString());
        return parts;
    }
}
