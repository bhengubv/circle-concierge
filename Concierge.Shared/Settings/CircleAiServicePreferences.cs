using System.Text.Json;

namespace Concierge.Shared.Settings;

public sealed record CircleAiServiceOptions(bool StreamAnswers = true, bool AgenticWork = true);

/// <summary>Request preferences, not model selection or service startup configuration.</summary>
public sealed class CircleAiServicePreferences(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public CircleAiServiceOptions Current { get; private set; } = Read(path);

    private static CircleAiServiceOptions Read(string path)
        => File.Exists(path)
            ? JsonSerializer.Deserialize<CircleAiServiceOptions>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Circle AI service preferences are empty.")
            : new();

    public Task SaveAsync(CircleAiServiceOptions options) => UpdateAsync(_ => options);

    public async Task UpdateAsync(Func<CircleAiServiceOptions, CircleAiServiceOptions> update)
    {
        await _gate.WaitAsync();
        var temporary = path + ".tmp";
        try
        {
            var options = update(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(options));
            File.Move(temporary, path, overwrite: true);
            Current = options;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }
}
