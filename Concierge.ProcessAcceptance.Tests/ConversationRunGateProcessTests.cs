using System.Diagnostics;

namespace Concierge.ProcessAcceptance.Tests;

public sealed class ConversationRunGateProcessTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"conversation-gate-process-{Guid.NewGuid():N}");

    [Fact]
    public async Task Separate_processes_serialize_turns_for_the_same_conversation()
    {
        Directory.CreateDirectory(_folder);
        var conversationId = Guid.NewGuid();
        var firstMarker = Path.Combine(_folder, "first-entered.txt");
        var secondMarker = Path.Combine(_folder, "second-entered.txt");
        using var first = StartWorker(conversationId, firstMarker, holdMs: 1_000);

        try
        {
            await WaitForMarkerAsync(first, firstMarker);
            using var second = StartWorker(conversationId, secondMarker, holdMs: 0);

            await Task.Delay(300);
            Assert.False(File.Exists(secondMarker), "The second host entered while the first host still held the conversation lock.");

            await WaitForWorkerAsync(first);
            await WaitForMarkerAsync(second, secondMarker);
            await WaitForWorkerAsync(second);
        }
        finally
        {
            if (!first.HasExited)
                first.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Operating_system_releases_the_conversation_lock_when_a_host_is_killed()
    {
        Directory.CreateDirectory(_folder);
        var conversationId = Guid.NewGuid();
        var firstMarker = Path.Combine(_folder, "crashed-host-entered.txt");
        var secondMarker = Path.Combine(_folder, "recovered-host-entered.txt");
        using var first = StartWorker(conversationId, firstMarker, holdMs: 30_000);

        try
        {
            await WaitForMarkerAsync(first, firstMarker);
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            using var second = StartWorker(conversationId, secondMarker, holdMs: 0);
            await WaitForMarkerAsync(second, secondMarker);
            await WaitForWorkerAsync(second);
        }
        finally
        {
            if (!first.HasExited)
                first.Kill(entireProcessTree: true);
        }
    }

    private Process StartWorker(Guid conversationId, string marker, int holdMs)
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
        foreach (var variable in start.Environment.Keys
                     .Where(name => name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
                         || name.StartsWith("COVERLET_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            start.Environment.Remove(variable);

        start.ArgumentList.Add(workerPath);
        start.ArgumentList.Add("conversation-gate");
        start.ArgumentList.Add(_folder);
        start.ArgumentList.Add(conversationId.ToString("D"));
        start.ArgumentList.Add(marker);
        start.ArgumentList.Add(holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the process acceptance worker.");
    }

    private static async Task WaitForWorkerAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"Process worker exited {process.ExitCode}: {error}");
        _ = await stdout;
    }

    private static async Task WaitForMarkerAsync(Process process, string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker))
        {
            timeout.Token.ThrowIfCancellationRequested();
            Assert.False(process.HasExited, "The process exited before entering its conversation run.");
            await Task.Delay(20, timeout.Token);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
