using System.Diagnostics;
using System.Text.Json;
using Concierge.Shared.Away;

namespace Concierge.ProcessAcceptance.Tests;

public sealed class AwayRequestLedgerProcessTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"away-process-receipts-{Guid.NewGuid():N}");

    [Fact]
    public async Task Completed_request_is_replayed_after_the_original_process_exits()
    {
        var requestId = Guid.NewGuid();

        var first = await RunWorkerAsync(requestId, "original answer", marker: null, delayMs: 0);
        var afterRestart = await RunWorkerAsync(requestId, "must not run", marker: null, delayMs: 0);

        Assert.True(first.Executed);
        Assert.False(afterRestart.Executed);
        Assert.Equal(first.Answer, afterRestart.Answer);
    }

    [Fact]
    public async Task Two_processes_delivering_the_same_request_execute_only_once()
    {
        var requestId = Guid.NewGuid();
        var marker = Path.Combine(_folder, "first-entered.txt");
        Directory.CreateDirectory(_folder);

        using var first = StartWorker(requestId, "first answer", marker, delayMs: 800);
        try
        {
            await WaitForMarkerAsync(first, marker);
            using var second = StartWorker(requestId, "second answer", marker: null, delayMs: 0);

            var outcomes = await Task.WhenAll(WaitForWorkerAsync(first), WaitForWorkerAsync(second));

            Assert.Single(outcomes, outcome => outcome.Executed);
            Assert.All(outcomes, outcome => Assert.Equal("first answer", outcome.Answer.Reply));
        }
        finally
        {
            if (!first.HasExited)
                first.Kill(entireProcessTree: true);
        }
    }

    private async Task<WorkerReply> RunWorkerAsync(Guid requestId, string answer, string? marker, int delayMs)
    {
        using var process = StartWorker(requestId, answer, marker, delayMs);
        return await WaitForWorkerAsync(process);
    }

    private Process StartWorker(Guid requestId, string answer, string? marker, int delayMs)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "process-host", "Concierge.Tests.ProcessHost.dll");
        Assert.True(File.Exists(workerPath), $"The process test worker was not copied to {workerPath}.");

        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Coverlet instruments the main testhost through inherited CLR profiler variables.
        // The helper is a separate process, not another instrumented copy of this suite.
        foreach (var variable in start.Environment.Keys
                     .Where(name => name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("COVERLET_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            start.Environment.Remove(variable);

        start.ArgumentList.Add(workerPath);
        start.ArgumentList.Add(_folder);
        start.ArgumentList.Add(requestId.ToString("D"));
        start.ArgumentList.Add("same-work");
        start.ArgumentList.Add(answer);
        start.ArgumentList.Add(marker ?? string.Empty);
        start.ArgumentList.Add(delayMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(start);
        Assert.NotNull(process);
        return process;
    }

    private static async Task<WorkerReply> WaitForWorkerAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"Process worker exited {process.ExitCode}: {error}");

        var output = await stdout;
        return JsonSerializer.Deserialize<WorkerReply>(output, JsonOptions)
            ?? throw new InvalidOperationException($"Process worker returned invalid JSON: {output}");
    }

    private static async Task WaitForMarkerAsync(Process process, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker))
        {
            timeout.Token.ThrowIfCancellationRequested();
            Assert.False(process.HasExited, "The first worker exited before executing the request.");
            await Task.Delay(20, timeout.Token);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private sealed record WorkerReply(bool Executed, AwayAnswer Answer);
}
