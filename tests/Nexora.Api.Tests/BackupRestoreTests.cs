using System.Diagnostics;
using FluentAssertions;
using Npgsql;

namespace Nexora.Api.Tests;
public sealed class BackupRestoreTests
{
    private static string Root()
    {
        for(var dir=new DirectoryInfo(AppContext.BaseDirectory);dir is not null;dir=dir.Parent)
            if(File.Exists(Path.Combine(dir.FullName,"Nexora.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found");
    }
    private static async Task<string> Run(string script,params string[] arguments)
    {
        var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-File",Path.Combine(Root(),"scripts",script)}.Concat(arguments))start.ArgumentList.Add(arg);
        using var p=Process.Start(start)!;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(timeout.Token);p.ExitCode.Should().Be(0,await error);return (await output).Trim();
    }
    [PgFact]
    public async Task A_real_isolated_archive_restores_rows_into_an_empty_disposable_database()
    {
        using var pg=new IsolatedPostgres();
        await using var source=new NpgsqlConnection(pg.ConnectionString);await source.OpenAsync();
        await using(var command=new NpgsqlCommand("CREATE TABLE recovery_probe(id integer PRIMARY KEY, value text NOT NULL); INSERT INTO recovery_probe VALUES(1,'synthetic recovery evidence')",source)) await command.ExecuteNonQueryAsync();
        var name="nexora_restore_"+Guid.NewGuid().ToString("N");
        await using(var command=new NpgsqlCommand("CREATE DATABASE "+name,source))await command.ExecuteNonQueryAsync();
        var directory=Path.Combine(Path.GetTempPath(),"nexora-backuptest-"+Guid.NewGuid().ToString("N"));
        var marker=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".marker");
        try
        {
            File.WriteAllText(marker,"nexora-disposable-restore-v1:"+Guid.NewGuid().ToString("N"));
            var manifest=await Run("Backup-NexoraDatabase.ps1","-PgBin",IsolatedPostgres.FindBin()!,"-Port",pg.Port.ToString(),"-Database","postgres","-OutputDirectory",directory);
            await Run("Restore-NexoraDisposableDatabase.ps1","-PgBin",IsolatedPostgres.FindBin()!,"-Port",pg.Port.ToString(),"-Database",name,"-Manifest",manifest,"-DisposableMarker",marker);
            var targetString=new NpgsqlConnectionStringBuilder(pg.ConnectionString){Database=name}.ConnectionString;
            await using var target=new NpgsqlConnection(targetString);await target.OpenAsync();
            await using var verify=new NpgsqlCommand("SELECT value FROM recovery_probe WHERE id=1",target);
            ((string)(await verify.ExecuteScalarAsync())!).Should().Be("synthetic recovery evidence");
        }
        finally
        {
            File.Delete(marker);
            var resolved=Path.GetFullPath(directory);
            if(!resolved.StartsWith(Path.Combine(Path.GetTempPath(),"nexora-backuptest-"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe test cleanup path");
            if(Directory.Exists(resolved))Directory.Delete(resolved,true);
        }
    }
}
