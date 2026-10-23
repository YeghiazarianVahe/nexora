using System.ComponentModel;
using System.Diagnostics;
using FluentAssertions;
using Nexora.Application.Jobs;
using Nexora.Contracts;
using Nexora.Helper.Actions;

namespace Nexora.Application.Tests;

public sealed class MessageActionTests
{
    private sealed class Sink : IJobSink
    {
        public List<(string Stream, string Line)> Lines { get; } = [];
        public Task Log(string stream, string line) { Lines.Add((stream, line)); return Task.CompletedTask; }
        public Task Progress(int percent) => Task.CompletedTask;
    }

    private static JobPayload Job(string? message = "Hello", string host = "PC-1") =>
        new(Guid.NewGuid(), Guid.NewGuid(), "session.message", host, "S-1-5-21-1-2-3-1000",
            message is null ? [] : new() { ["message"] = message }, 30, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2), "n");

    [Fact]
    public void Message_dispatch_does_not_require_a_remote_SYSTEM_task()
    {
        ActionRegistry.Find("session.message").Should().NotBeOfType<SystemCommandAction>();
        ActionCatalog.Find("session.message")!.Method.Should().Be(ExecutionMethod.Local);
    }

    [Theory]
    [InlineData("Hello world")]
    [InlineData("It's 5 pm'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("/server:OTHER /time:1")]
    [InlineData("Quotes \" and $env:USERNAME & | %USERNAME% ` text")]
    [InlineData("\u0542\u0561\u0580\u0587\u0582")]
    public async Task Message_and_target_are_literal_arguments_with_no_shell_or_remote_task(string message)
    {
        ProcessStartInfo? actual = null;
        var action = new MessageAction((start, _) => { actual = start; return Task.FromResult(new MessageCommandResult(0, "accepted", "")); });
        var result = await action.RunAsync(Job(message), new Sink(), default);
        result.State.Should().Be("Succeeded");
        result.Summary.Should().Contain("accepted").And.Contain("acknowledgement is not tracked");
        actual!.FileName.Should().Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msg.exe"));
        actual.UseShellExecute.Should().BeFalse();
        actual.CreateNoWindow.Should().BeTrue();
        actual.ArgumentList.Should().Equal("*", "/server:PC-1", "/time:60", "/v", "Nexora: " + message);
        actual.Arguments.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line1\nline2")]
    [InlineData("text\0hidden")]
    public async Task Invalid_messages_never_start_a_process(string? message)
    {
        var called = false;
        var action = new MessageAction((_, _) => { called = true; return Task.FromResult(new MessageCommandResult(0, "", "")); });
        (await action.RunAsync(Job(message), new Sink(), default)).State.Should().Be("Failed");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task Oversized_messages_and_invalid_hosts_never_start_a_process()
    {
        var action = new MessageAction((_, _) => throw new InvalidOperationException("Must not start"));
        (await action.RunAsync(Job(new string('x', 256)), new Sink(), default)).State.Should().Be("Failed");
        (await action.RunAsync(Job(host: "PC-1 & calc"), new Sink(), default)).State.Should().Be("Failed");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(1722)]
    public async Task Native_failure_is_not_reported_as_success_and_keeps_diagnostics(int exit)
    {
        var sink = new Sink();
        var action = new MessageAction((_, _) => Task.FromResult(new MessageCommandResult(exit, "session diagnostics", "The request is not supported. (0x80070032)")));
        var result = await action.RunAsync(Job(), sink, default);
        result.State.Should().Be("Failed");
        result.ExitCode.Should().Be(exit);
        result.Summary.Should().Contain($"exit {exit}");
        sink.Lines.Should().Contain(("stderr", "The request is not supported. (0x80070032)"));
    }

    [Fact]
    public async Task Command_start_failure_is_actionable()
    {
        var action = new MessageAction((_, _) => throw new Win32Exception(2));
        var result = await action.RunAsync(Job(), new Sink(), default);
        result.State.Should().Be("Failed");
        result.Summary.Should().Contain("could not start").And.Contain("Windows error 2");
    }

    [Fact]
    public async Task Internal_timeout_is_a_failed_unconfirmed_delivery()
    {
        var action = new MessageAction(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(0, "", ""); }, TimeSpan.FromMilliseconds(30));
        var result = await action.RunAsync(Job(), new Sink(), default);
        result.State.Should().Be("Failed");
        result.Summary.Should().Contain("timed out").And.Contain("could not be confirmed");
    }

    [Fact]
    public async Task Operator_cancellation_is_preserved()
    {
        using var cts = new CancellationTokenSource();
        var action = new MessageAction((_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(new MessageCommandResult(0, "", "")); });
        await FluentActions.Awaiting(() => action.RunAsync(Job(), new Sink(), cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }
}
