using System.IO.Compression;
using System.Text.Json;

namespace Concierge.Shared.Media;

/// <summary>Local prepared packages, including packages the person opens from Files.</summary>
public static class SleptOnPackageLibrary
{
    private const long MaxBytes = 50L * 1024 * 1024;

    public static IReadOnlyList<SleptOnMediaPackage> Recent(string folder, string kind)
    {
        if (!Directory.Exists(folder)) return [];
        var found = new List<SleptOnMediaPackage>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.zip")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(50))
        {
            try
            {
                var package = ReadInfo(path);
                if (package.Kind == kind) found.Add(package);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException
                or KeyNotFoundException or InvalidOperationException or ArgumentException) { }
            if (found.Count == 30) break;
        }
        return found;
    }

    public static async Task<SleptOnMediaPackage> ImportAsync(Stream source, string folder,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(folder)) throw new ArgumentException("The package folder must be absolute.", nameof(folder));
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".import-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) throw new InvalidDataException("The SleptON! package is too large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            await SleptOnPackageIdentity.ComputeAsync(temporary, cancellationToken);
            var info = ReadInfo(temporary);
            var final = Path.Combine(folder, $"{info.Kind}-{Guid.NewGuid():N}.zip");
            File.Move(temporary, final);
            return info with { Path = final };
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static SleptOnMediaPackage ReadInfo(string path)
    {
        if (new FileInfo(path).Length is <= 0 or > MaxBytes)
            throw new InvalidDataException("The SleptON! package size is invalid.");
        using var archive = ZipFile.OpenRead(path);
        var manifest = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The SleptON! package has no manifest.");
        if (manifest.Length is <= 0 or > 64 * 1024)
            throw new InvalidDataException("The SleptON! package manifest is invalid.");
        using var stream = manifest.Open();
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("This SleptON! package version is unsupported.");
        var kind = root.GetProperty("kind").GetString();
        var title = root.GetProperty("title").GetString();
        if (kind is not ("audio-draft" or "book-trailer") || string.IsNullOrWhiteSpace(title))
            throw new InvalidDataException("This SleptON! package has invalid details.");
        return new(path, kind, title);
    }
}

public interface ISleptOnPackagePicker
{
    Task<SleptOnMediaPackage?> PickAsync(string outputRoot, CancellationToken cancellationToken = default);
}
