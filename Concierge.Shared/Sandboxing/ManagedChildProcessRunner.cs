using System.Diagnostics;

namespace Concierge.Shared.Sandboxing;

/// <summary>Result captured from a bounded child process.</summary>
public sealed record ManagedChildProcessResult(int ExitCode, string Output);

/// <summary>
/// Runs short-lived media and speech workers with drained output and cancellation-safe
/// termination. Windows workers are born inside a memory- and process-limited job.
/// </summary>
public static class ManagedChildProcessRunner
{
    private static readonly SemaphoreSlim WindowsWorkerSlot = new(1, 1);
    private const long SystemReserveBytes = 1536L * 1024 * 1024;
    private const long MinimumWorkerBytes = 512L * 1024 * 1024;
    private const long MaximumWorkerBytes = 4L * 1024 * 1024 * 1024;

    public static async Task<ManagedChildProcessResult> RunCapturedAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (OperatingSystem.IsWindows())
        {
            await WindowsWorkerSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunWindowsAsync(executable, arguments, workingDirectory, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                WindowsWorkerSlot.Release();
            }
        }

        return await RunPortableAsync(executable, arguments, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<ManagedChildProcessResult> RunWindowsAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var availableMemory = WindowsAvailableMemory.MeasureAvailablePhysicalBytes()
            ?? throw new InvalidOperationException("Available memory could not be measured; the worker was not started.");
        var headroom = availableMemory - SystemReserveBytes;
        if (headroom < MinimumWorkerBytes)
            throw new InvalidOperationException("There is not enough free memory to start another managed worker.");
        var memoryBudget = Math.Min(headroom, MaximumWorkerBytes);

        using var job = new WindowsJobObjectSandbox(
            memoryCapBytes: memoryBudget,
            processCap: 2,
            jobMemoryCapBytes: memoryBudget);
        job.OpenJob();
        using var child = ConfinedProcess.StartInJob(job.Job, executable, arguments, workingDirectory);
        using var outputReader = new StreamReader(child.StandardOutput, leaveOpen: true);
        using var errorReader = new StreamReader(child.StandardError, leaveOpen: true);
        var standardOutput = outputReader.ReadToEndAsync();
        var standardError = errorReader.ReadToEndAsync();

        try
        {
            await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Dispose();
            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            throw;
        }

        return new ManagedChildProcessResult(
            child.ExitCode,
            await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false));
    }

    private static async Task<ManagedChildProcessResult> RunPortableAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The worker would not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            throw;
        }

        return new ManagedChildProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false));
    }
}
