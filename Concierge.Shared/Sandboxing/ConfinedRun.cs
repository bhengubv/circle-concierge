using System.Diagnostics;
using System.Text;

namespace Concierge.Shared.Sandboxing;

/// <summary>
/// Running one program, confined, on whichever platform this is.
///
/// The two paths are the ones `AgentHarnessService` already picks between: on Windows the
/// process is born inside a job object, and everywhere else the sandbox prepares the start
/// info and confines the process once it is running. The choice, the timeout, the kill and
/// the disposal are the same either way, and they are here so a second caller does not have
/// to get them right a second time.
///
/// **A sandbox that refuses to build fails the call.** The alternative is running the command
/// unconfined after deciding it should not be, which is worse than not running it at all.
/// </summary>
public static class ConfinedRun
{
    /// <summary>What happened.</summary>
    /// <param name="Ran">Whether the program actually ran to an exit.</param>
    /// <param name="ExitCode">Its exit code, when it ran.</param>
    /// <param name="Output">What it printed, standard output then standard error.</param>
    /// <param name="Problem">Why it did not run, when it did not.</param>
    public sealed record Result(bool Ran, int ExitCode, string Output, string? Problem);

    public static async Task<Result> Async(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan runFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(runFor);

        var sandbox = CodeSandbox.ForCurrentPlatform();

        try
        {
            if (sandbox is WindowsJobObjectSandbox windows && OperatingSystem.IsWindows())
            {
                var confined = await ConfinedProcess
                    .RunAsync(windows.OpenJob(), executable, arguments, workingDirectory, timeout.Token)
                    .ConfigureAwait(false);

                return new Result(true, confined.ExitCode, confined.Output, null);
            }

            using var process = new Process();
            process.StartInfo.FileName = executable;

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            sandbox.Prepare(process.StartInfo, workingDirectory);

            // Set after Prepare, which must not quietly undo them: what the program printed is
            // the whole point of running it.
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            process.Start();
            sandbox.Confine(process);

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var said = new StringBuilder()
                .Append(await output.ConfigureAwait(false))
                .Append(await errors.ConfigureAwait(false))
                .ToString();

            return new Result(true, process.ExitCode, said, null);
        }
        catch (OperationCanceledException)
        {
            return new Result(false, -1, string.Empty,
                cancellationToken.IsCancellationRequested
                    ? "Stopped."
                    : $"It was still going after {runFor.TotalMinutes:0.#} minutes, so it was stopped.");
        }
        catch (InvalidOperationException problem)
        {
            return new Result(false, -1, string.Empty, problem.Message);
        }
        catch (System.ComponentModel.Win32Exception problem)
        {
            return new Result(false, -1, string.Empty, problem.Message);
        }
        finally
        {
            (sandbox as IDisposable)?.Dispose();
        }
    }
}
