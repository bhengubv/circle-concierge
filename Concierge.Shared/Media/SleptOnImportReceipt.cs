using System.Text.Json;

namespace Concierge.Shared.Media;

public sealed record SleptOnImportError(string Code, string Message, string? Path);
public sealed record SleptOnImportReceipt(string Status, string? DraftId, string? RevisionId,
    IReadOnlyList<SleptOnImportError> Errors);

/// <summary>Reads a small, untrusted receipt chosen by the person from SleptON!.</summary>
public static class SleptOnImportReceiptReader
{
    private const int MaxBytes = 64 * 1024;

    public static async Task<SleptOnImportReceipt> ReadAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new InvalidDataException("The SleptON! result file is too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;
        using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            throw new InvalidDataException("This SleptON! result version is not supported.");
        var status = RequiredString(root, "status");
        if (status == "accepted")
        {
            var draftId = RequiredString(root, "draftId");
            var revisionId = RequiredString(root, "revisionId");
            if (!Guid.TryParse(draftId, out _) || revisionId.Length != 64
                || revisionId.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("The accepted SleptON! result has invalid draft identifiers.");
            return new(status, draftId, revisionId, []);
        }
        if (status != "rejected" || !root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
            throw new InvalidDataException("The SleptON! result has no valid outcome.");
        var parsed = new List<SleptOnImportError>();
        foreach (var error in errors.EnumerateArray())
        {
            var code = RequiredString(error, "code");
            var message = RequiredString(error, "message");
            if (code.Length > 64 || message.Length > 500)
                throw new InvalidDataException("The SleptON! result has an invalid error.");
            var path = error.TryGetProperty("path", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (path?.Length > 260)
                throw new InvalidDataException("The SleptON! result has an invalid path.");
            parsed.Add(new(code, message, path));
        }
        return new(status, null, null, parsed);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"The SleptON! result is missing {name}.");
        return value.GetString()!;
    }
}

public interface ISleptOnImportReceiptPicker
{
    Task<SleptOnImportReceipt?> PickAsync(CancellationToken cancellationToken = default);
}
