using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

/// <summary>power.restart, power.shutdown, net.diag, disk.cleanup: bodies are inspected as text; nothing is executed.</summary>
public sealed class PowerAndDiagCommandTests
{
    private static (string? Body, string? Error) Build(string code, params (string, string)[] p) =>
        SystemCommandAction.Commands[code].Build(p.ToDictionary(x => x.Item1, x => x.Item2));

    [Theory]
    [InlineData("power.restart", "/r")]
    [InlineData("power.shutdown", "/s")]
    public void Power_actions_schedule_with_a_countdown_and_quote_the_message(string code, string flag)
    {
        var (body, error) = Build(code, ("delayMinutes", "5"), ("message", "it's \"$env:USERNAME\" time"));
        error.Should().BeNull();
        body.Should().Contain($"shutdown.exe\" {flag} /t 300 /c 'it''s \"$env:USERNAME\" time'");
    }

    [Fact]
    public void Power_actions_have_a_ten_second_minimum_and_reject_bad_input()
    {
        Build("power.restart").Body.Should().Contain("/t 10 ");
        foreach (var bad in new[] { "61", "-1", "abc", "1.5", "5; calc" }) Build("power.shutdown", ("delayMinutes", bad)).Error.Should().NotBeNull(bad);
        Build("power.restart", ("message", "line\nbreak")).Error.Should().NotBeNull();
        Build("power.restart", ("message", new string('a', 256))).Error.Should().NotBeNull();
    }

    [Fact]
    public void Power_actions_keep_every_safeguard_in_the_catalog()
    {
        foreach (var code in new[] { "power.restart", "power.shutdown" })
        {
            var d = ActionCatalog.Find(code)!;
            d.Implemented.Should().BeTrue();
            d.IsPower.Should().BeTrue();          // protected hosts blocked, reason + typed confirmation required
            d.Risk.Should().Be(RiskLevel.High);
            d.SupportsCancel.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("power.restart", 0, "Succeeded")]
    [InlineData("power.restart", 1190, "Warning")]
    [InlineData("power.shutdown", 5, "Failed")]
    public void Power_exit_codes_are_reported_honestly(string code, int exit, string state) =>
        SystemCommandAction.Commands[code].Exit!(exit).State.Should().Be(state);

    [Fact]
    public void Diagnostics_and_cleanup_are_fixed_bodies_that_touch_nothing_outside_their_scope()
    {
        var diag = Build("net.diag").Body!;
        diag.Should().NotContain("Remove-").And.NotContain("Set-").And.NotContain("shutdown");
        var clean = Build("disk.cleanup").Body!;
        clean.Should().Contain("-LiteralPath $dir").And.Contain("AddDays(-7)").And.Contain("$env:SystemRoot 'Temp'");
        clean.Should().NotContain("Users").And.NotContain("SoftwareDistribution");
    }

    [Fact]
    public async Task Every_built_in_body_is_syntactically_valid_PowerShell_without_being_executed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexora-parse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var parameters = new Dictionary<string, string> { ["restartNow"] = "true", ["delayMinutes"] = "1", ["message"] = "hello", ["source"] = @"\srv\share\install.wim" };
            foreach (var (code, cmd) in SystemCommandAction.Commands)
            {
                var (body, error) = cmd.Build(parameters);
                (body ?? error).Should().NotBeNull(code);
                if (body is not null) File.WriteAllText(Path.Combine(dir, code + ".ps1"), body);
            }
            var script = "$bad = 0; foreach ($f in Get-ChildItem -LiteralPath $args[0] -Filter *.ps1) { $e = $null; $t = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$t, [ref]$e); if ($e) { $bad++; Write-Output ($f.Name + ': ' + $e[0].Message) } }; exit $bad";
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            script = "$dir = '" + dir + "'; " + script.Replace("$args[0]", "$dir");
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync(); var output = await p.StandardOutput.ReadToEndAsync() + await errTask;
            await p.WaitForExitAsync();
            p.ExitCode.Should().Be(0, output);
        }
        finally { Directory.Delete(dir, true); }
    }
}
