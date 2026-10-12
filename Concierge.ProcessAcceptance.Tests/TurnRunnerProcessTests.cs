using System.Diagnostics;
using System.Text.Json;
using Concierge.Shared.Chat;

namespace Concierge.ProcessAcceptance.Tests;

public sealed class TurnRunnerProcessTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"turn-run-process-{Guid.NewGuid():N}");

    [Fact]
    public async Task Completed_run_replays_its_receipt_after_the_original_process_exits()
    {
        Directory.CreateDirectory(_folder);
        var databasePath = Path.Combine(_folder, "chat.db");
        var conversationId = await CreateConversationAsync(databasePath);
        var runId = Guid.NewGuid();

        var first = await RunWorkerAsync(databasePath, conversationId, runId, "original answer");
        var afterRestart = await RunWorkerAsync(databasePath, conversationId, runId, "must not run");

        Assert.True(first.Executed);
        Assert.Equal("original answer", first.Result.Text);
        Assert.False(afterRestart.Executed);
        Assert.Equal("original answer", afterRestart.Result.Text);
        Assert.Null(afterRestart.Result.Stopped);
    }

    [Fact]
    public async Task Interrupted_run_is_visible_after_host_is_killed_and_is_not_reexecuted()
    {
        Directory.CreateDirectory(_folder);
        var databasePath = Path.Combine(_folder, "chat.db");
        var conversationId = await CreateConversationAsync(databasePath);
        var runId = Guid.NewGuid();
        var marker = Path.Combine(_folder, "runtime-entered.txt");

        using var interrupted = StartWorker(databasePath, conversationId, runId, "discarded", marker, block: true);
        try
        {
            await WaitForMarkerAsync(interrupted, marker);
            interrupted.Kill(entireProcessTree: true);
            await interrupted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            var recovered = await RunWorkerAsync(databasePath, conversationId, runId, "must not run");

            Assert.False(recovered.Executed);
            Assert.Null(recovered.Result.Text);
            Assert.Contains("interrupted", recovered.Result.Stopped, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!interrupted.HasExited)
                interrupted.Kill(entireProcessTree: true);
        }
    }

    private async Task<Guid> CreateConversationAsync(string databasePath)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "process-host", "Concierge.Tests.ProcessHost.dll");
        Assert.True(File.Exists(workerPath), $"The process test worker was not copied to {workerPath}.");
        var start = CreateProcessStart(workerPath);
        start.ArgumentList.Add("turn-create");
        start.ArgumentList.Add(databasePath);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the TurnRunner setup worker.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"Process setup worker exited {process.ExitCode}: {error}");
        return Guid.Parse((await stdout).Trim());
    }

    private Task<WorkerReply> RunWorkerAsync(
        string databasePath,
        Guid conversationId,
        Guid runId,
        string answer)
        => WaitForWorkerAsync(StartWorker(databasePath, conversationId, runId, answer, marker: "", block: false));

    private Process StartWorker(
        string databasePath,
        Guid conversationId,
        Guid runId,
        string answer,
        string marker,
        bool block)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "process-host", "Concierge.Tests.ProcessHost.dll");
        Assert.True(File.Exists(workerPath), $"The process test worker was not copied to {workerPath}.");

        var start = CreateProcessStart(workerPath);
        start.ArgumentList.Add("turn-run");
        start.ArgumentList.Add(databasePath);
        start.ArgumentList.Add(Path.Combine(_folder, "locks"));
        start.ArgumentList.Add(conversationId.ToString("D"));
        start.ArgumentList.Add(runId.ToString("D"));
        start.ArgumentList.Add(answer);
        start.ArgumentList.Add(marker);
        start.ArgumentList.Add(block.ToString());

        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the TurnRunner process worker.");
    }

    private static ProcessStartInfo CreateProcessStart(string workerPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var variable in start.Environment.Keys
                     .Where(name => name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("COVERLET_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            start.Environment.Remove(variable);
        start.ArgumentList.Add(workerPath);
        return start;
    }

    private static async Task<WorkerReply> WaitForWorkerAsync(Process process)
    {
        using (process)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var error = await stderr;
            Assert.True(process.ExitCode == 0, $"Process worker exited {process.ExitCode}: {error}");

            var output = await stdout;
            return JsonSerializer.Deserialize<WorkerReply>(output, JsonOptions)
                ?? throw new InvalidOperationException($"Process worker returned invalid JSON: {output}");
        }
    }

    private static async Task WaitForMarkerAsync(Process process, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker))
        {
            timeout.Token.ThrowIfCancellationRequested();
            Assert.False(process.HasExited, "The run process exited before entering the runtime.");
            await Task.Delay(20, timeout.Token);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private sealed record WorkerReply(bool Executed, TurnResult Result);
}
