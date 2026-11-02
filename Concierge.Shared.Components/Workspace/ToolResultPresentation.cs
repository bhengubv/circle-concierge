using System.Text.Json;

namespace Concierge.Shared.Components.Workspace;

/// <summary>Turns a durable tool result into a readable activity row without changing model history.</summary>
public sealed record ToolResultPresentation(string Headline, string Details, bool? Succeeded)
{
    public static ToolResultPresentation From(string content)
    {
        if (content.StartsWith("```tool-result", StringComparison.Ordinal))
        {
            var start = content.IndexOf('\n');
            var end = content.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                try
                {
                    using var payload = JsonDocument.Parse(content[(start + 1)..end]);
                    var root = payload.RootElement;
                    if (root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        && root.TryGetProperty("success", out var success)
                        && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        var succeeded = success.GetBoolean();
                        var detailKey = succeeded ? "output" : "failure";
                        var details = root.TryGetProperty(detailKey, out var detail) && detail.ValueKind == JsonValueKind.String
                            ? detail.GetString() ?? string.Empty : string.Empty;
                        var readableName = name.GetString()!.Replace('_', ' ').Trim();
                        if (readableName.Length > 0)
                            readableName = char.ToUpperInvariant(readableName[0]) + readableName[1..];
                        return new ToolResultPresentation(
                            $"{readableName} · {(succeeded ? "Completed" : "Needs attention")}", details, succeeded);
                    }
                }
                catch (JsonException) { /* A malformed historical receipt is still readable below. */ }
            }
        }

        var headline = content.Split('\n')[0];
        var rest = content.Length > headline.Length ? content[(headline.Length + 1)..].Trim() : string.Empty;
        return new ToolResultPresentation(headline, rest, null);
    }
}
