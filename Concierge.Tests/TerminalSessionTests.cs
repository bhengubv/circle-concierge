using Concierge.Shared.Terminals;

namespace Concierge.Tests;

/// <summary>
/// What a persistent terminal must do (parity feature 41): stay open across calls so state
/// survives between them.
/// </summary>
/// <remarks>
/// <c>RunCommandAsync</c> spawns a process and exits, so nothing carries over — not the
/// working directory, not an environment variable, not a running REPL. A session that stays
/// open is a different capability, and the one anybody doing real work needs.
/// </remarks>
public sealed class TerminalSessionTests : IDisposable
{
    private readonly string _workspace;
    private readonly TerminalRuntime _terminals;

    public TerminalSessionTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"concierge-terminal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _terminals = new TerminalRuntime(_workspace);
    }

    public void Dispose()
    {
        _terminals.Dispose();
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp directory.
        }
    }

    [Fact]
    public async Task A_session_can_be_opened()
    {
        var id = await _terminals.OpenAsync();

        Assert.Contains(id, _terminals.Sessions);
    }

    [Fact]
    public async Task A_command_produces_output()
    {
        var id = await _terminals.OpenAsync();

        var output = await _terminals.SendAsync(id, "echo hello-from-the-terminal");

        Assert.Contains("hello-from-the-terminal", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_survives_between_commands()
    {
        // The whole point: a variable set in one call is still there in the next. With
        // one process per command this is impossible.
        var id = await _terminals.OpenAsync();

        await _terminals.SendAsync(id, "set CONCIERGE_MARKER=carried-over");
        var output = await _terminals.SendAsync(id, "echo %CONCIERGE_MARKER%");

        Assert.Contains("carried-over", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_working_directory_survives_between_commands()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "subdir"));
        var id = await _terminals.OpenAsync();

        await _terminals.SendAsync(id, "cd subdir");
        var output = await _terminals.SendAsync(id, "cd");

        Assert.Contains("subdir", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_starts_in_the_workspace()
    {
        var id = await _terminals.OpenAsync();

        var output = await _terminals.SendAsync(id, "cd");

        Assert.Contains(Path.GetFileName(_workspace), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_sessions_do_not_share_state()
    {
        var first = await _terminals.OpenAsync();
        var second = await _terminals.OpenAsync();

        await _terminals.SendAsync(first, "set ONLY_IN_FIRST=yes");
        var output = await _terminals.SendAsync(second, "echo [%ONLY_IN_FIRST%]");

        Assert.DoesNotContain("yes", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_session_is_gone()
    {
        var id = await _terminals.OpenAsync();

        await _terminals.CloseAsync(id);

        Assert.DoesNotContain(id, _terminals.Sessions);
    }

    [Fact]
    public async Task Sending_to_a_session_that_is_not_open_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _terminals.SendAsync("no-such-session", "echo hello"));
    }

    [Fact]
    public async Task Closing_a_session_that_is_not_open_is_harmless()
    {
        await _terminals.CloseAsync("no-such-session");
    }

    [Fact]
    public async Task A_command_that_produces_nothing_returns_empty_rather_than_hanging()
    {
        var id = await _terminals.OpenAsync();

        var output = await _terminals.SendAsync(id, "rem this command says nothing");

        Assert.NotNull(output);
    }

    [Fact]
    public async Task Disposing_the_runtime_closes_every_session()
    {
        var runtime = new TerminalRuntime(_workspace);
        await runtime.OpenAsync();
        await runtime.OpenAsync();

        runtime.Dispose();

        Assert.Empty(runtime.Sessions);
    }
}
