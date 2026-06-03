using System.Runtime.InteropServices;
using System.Text.Json;

namespace Concierge.Shared.Settings;

/// <summary>
/// File-backed <see cref="IConciergeSecretStore"/>. Saves to
/// <c>%LocalAppData%/Concierge/secrets.json</c> on Windows (XDG-style equivalent on macOS /
/// Linux). The on-disk format is a flat JSON dictionary so a human can inspect it with any
/// text editor — encryption is NOT applied here because cross-platform symmetric key
/// management is fragile, and the file lives under a per-user directory with restrictive
/// POSIX permissions where the OS supports it. Treat the file like any other dev secret:
/// don't sync it to git, don't share it.
/// </summary>
public sealed class LocalSecretStore : IConciergeSecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalSecretStore(string? path = null)
    {
        Path = path ?? ResolveDefaultPath();
    }

    public string Path { get; }

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous);
            var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return dict ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Malformed file — return empty rather than throw so the UI keeps working. The
            // user can fix it via a fresh save.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = File.Exists(Path)
                ? await LoadInternalAsync(cancellationToken).ConfigureAwait(false)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (key, value) in secrets)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    existing.Remove(key);
                }
                else
                {
                    existing[key] = value;
                }
            }

            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = Path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, existing, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            TryRestrictPermissions(temp);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadInternalAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);
        try
        {
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string ResolveDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = System.IO.Path.Combine(Directory.GetCurrentDirectory(), ".concierge-artifacts");
        }
        return System.IO.Path.Combine(root, "Concierge", "secrets.json");
    }

    private static void TryRestrictPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best-effort.
        }
    }
}

public static class ConciergeSecretStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IConciergeSecretStore"/> + layers the saved secrets onto
    /// <c>IConfiguration</c> so wired runtimes pick them up at next startup.
    /// </summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddConciergeSecretStore(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .TryAddSingleton<IConciergeSecretStore>(services, _ => new LocalSecretStore());
        return services;
    }
}
