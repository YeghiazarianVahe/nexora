namespace Nexora.Contracts;

/// <summary>The one place the helper learns where Nexora lives. The pilot runs on wks-111, port 5090.</summary>
public static class HelperDefaults
{
    public const int Port = 5090;
    public const string Server = "http://wks-111:5090";

    /// <summary>Name of the per-user autostart value (HKCU ... Run).</summary>
    public const string RunValueName = "NexoraHelper";
}
