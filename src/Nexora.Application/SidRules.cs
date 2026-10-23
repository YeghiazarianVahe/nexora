using System.Text.RegularExpressions;

namespace Nexora.Application;

public static partial class SidRules
{
    [GeneratedRegex(@"^S-1-\d{1,3}(-\d{1,10}){1,15}$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool IsValid(string? sid) => sid is { Length: <= 184 } && Pattern().IsMatch(sid);
}
