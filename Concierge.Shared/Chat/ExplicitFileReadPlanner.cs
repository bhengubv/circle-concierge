using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Chat;

/// <summary>
/// Starts a read-only tool call when the person explicitly asks to read named local files.
/// This does not interpret a model response or file contents as an instruction to read more.
/// The harness still enforces its trusted-root and protected-path rules.
/// </summary>
internal static partial class ExplicitFileReadPlanner
{
    private const int MaxFiles = 4;

    // Attachment text is prepended to a turn. Requiring the person's turn to start
    // with a read request prevents text inside an attached document from starting one.
    [GeneratedRegex(@"\A\s*(?:please\s+)?(?:read|open|inspect|review)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadIntent();

    [GeneratedRegex("\"(?<quoted>[A-Za-z]:[\\\\/][^\"]+)\"|(?<plain>[A-Za-z]:[\\\\/][^\\s,;\\)\\]}>]+)", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    public static IReadOnlyList<PlannedToolCall> Plan(string userText, string workspaceRoot)
    {
        if (!ReadIntent().IsMatch(userText)) return [];

        var root = Path.GetFullPath(workspaceRoot);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in WindowsPath().Matches(userText))
        {
            var raw = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
            if (raw.Length == 0 || raw.Any(char.IsControl)) continue;

            string full;
            try { full = Path.GetFullPath(raw); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            relativePaths.Add(Path.GetRelativePath(root, full));
            if (relativePaths.Count == MaxFiles) break;
        }

        return relativePaths.Select(path => new PlannedToolCall("read_file_lines",
            new JsonObject { ["path"] = path, ["offset"] = 1, ["limit"] = 300 })).ToArray();
    }
}
