using System.Diagnostics;

namespace Concierge.Shared.Design;

/// <summary>Runs an explicitly installed Infinigen executable to create a private Studio draft.</summary>
public sealed class InfinigenDrafts(string outputRoot, string? executable)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(executable) &&
        Path.IsPathFullyQualified(executable) && File.Exists(executable);

    public string Status => IsAvailable
        ? "Infinigen executable found. Make a private 3D draft to check this computer's generator setup."
        : "3D generation needs Infinigen installed on this computer. Set CONCIERGE_INFINIGEN_EXE to its executable path.";

    public async Task<string> CreateSofaAsync(int seed, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new InvalidOperationException(Status);
        if (seed < 0) throw new ArgumentOutOfRangeException(nameof(seed));

        Directory.CreateDirectory(outputRoot);
        var destination = Path.Combine(outputRoot, $"sofa-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);

        using var process = new Process { StartInfo = new ProcessStartInfo(executable!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        } };
        foreach (var argument in new[] { "sofa_rand", "object_demo", "render_cycles", "--seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "--output", destination })
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start()) throw new InvalidOperationException("Infinigen could not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            await process.WaitForExitAsync(timeout.Token);
            var log = (await output) + Environment.NewLine + (await errors);
            await File.WriteAllTextAsync(Path.Combine(destination, "generation.log"), log, cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Infinigen exited with code {process.ExitCode}. See generation.log in {destination}.");
            if (!Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
                    .Any(file => !file.EndsWith("generation.log", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Infinigen produced no draft files. See generation.log in {destination}.");
            return destination;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
