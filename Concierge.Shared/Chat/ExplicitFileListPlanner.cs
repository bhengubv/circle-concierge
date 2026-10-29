using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Chat;

/// <summary>
/// Recognizes a person's direct request for the files in a named workspace folder.
/// The tool, rather than model text, supplies the answer. The harness still decides
/// which paths may be listed.
/// </summary>
internal static partial class ExplicitFileListPlanner
{
    [GeneratedRegex("^\\s*(?:(?:what|which)\\s+files\\s+are\\s+in|(?:please\\s+)?(?:list|show)\\s+(?:me\\s+)?(?:the\\s+)?files\\s+(?:in|under))\\s+(?:the\\s+)?(?<path>\\\"[^\\\"]+\\\"|'[^']+'|`[^`]+`|[^\\s?]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileListRequest();

    public static PlannedToolCall? Plan(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return null;

        var match = FileListRequest().Match(userText);
        if (!match.Success) return null;

        var path = match.Groups["path"].Value.Trim('"', '\'', '`').TrimEnd('.', ',', ';', ':', '!', '?');
        if (path.Length == 0 || path.Any(char.IsControl) || path is "folder" or "directory")
            return null;

        // Keep the relative path as the person wrote it. The harness resolves it
        // against its trusted root and rejects traversal or protected folders.
        return new PlannedToolCall("list_files", new JsonObject { ["path"] = path });
    }
}
