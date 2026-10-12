using System.Diagnostics;
using Concierge.Shared.Sandboxing;

namespace Concierge.Tests;

public sealed class ManagedChildProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "concierge-managed-child-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Windows_worker_captures_both_pipes_and_stops_its_descendants_on_cancellation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_root);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershell));

        var commandProcessor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var fastExit = await ManagedChildProcessRunner.RunCapturedAsync(
            commandProcessor, ["/c", "exit 7"], _root);
        Assert.Equal(7, fastExit.ExitCode);

        var result = await ManagedChildProcessRunner.RunCapturedAsync(
            powershell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
             "[Console]::Out.WriteLine('stdout'); [Console]::Error.WriteLine('stderr')"],
            _root);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stdout", result.Output);
        Assert.Contains("stderr", result.Output);

        var marker = Path.Combine(_root, "child.pid");
        var script = Path.Combine(_root, "spawn.ps1");
        await File.WriteAllTextAsync(script, """
            param($markerPath)
            $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') `
              -ArgumentList @('-NoLogo', '-NoProfile', '-Command', 'Start-Sleep -Seconds 60') `
              -PassThru -WindowStyle Hidden
            [IO.File]::WriteAllText($markerPath, $child.Id.ToString())
            Start-Sleep -Seconds 60
            """);

        using var stopping = new CancellationTokenSource();
        var running = ManagedChildProcessRunner.RunCapturedAsync(
            powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, marker],
            _root, stopping.Token);
        using var markerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker))
            await Task.Delay(25, markerTimeout.Token);

        using var childProcess = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
        Assert.False(childProcess.HasExited);
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await childProcess.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.True(childProcess.HasExited);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
