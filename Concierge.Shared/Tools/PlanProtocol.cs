using System.Text.Json;
using System.Text.RegularExpressions;

namespace Concierge.Shared.Tools;

/// <summary>
/// A plan the assistant states before doing multi-step work.
///
/// The same fenced-block shape the tool protocol uses, for the same reason:
/// any model can emit it without provider-native support, and the conversation
/// stored in SQLite stays normalised either way.
///
///   ```plan
///   ["Read the config", "Change the port", "Run the tests"]
///   ```
///
/// Why this exists. A run of six tool calls currently arrives as six chips in
/// a row, and there is no way to tell from the outside whether it is halfway
/// through something sensible or has been going in circles since step two.
/// The tool loop already stops after a fixed number of rounds precisely
/// because nobody can see what it is doing.
///
/// A plan changes what the person is asked to trust. Instead of watching
/// actions and inferring intent, they read the intent first and watch it being
/// crossed off — which is also the moment to stop it, before it acts rather
/// than after.
/// </summary>
public static class PlanProtocol
{
    private static readonly Regex PlanBlockRegex = new(
        @"```plan\s*\n(?<body>[\s\S]*?)\n```",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// How many steps are worth showing. A model asked for a plan will
    /// occasionally produce forty; a list that long is not a plan, and a
    /// thread full of it hides the conversation.
    /// </summary>
    public const int MaxSteps = 12;

    /// <summary>
    /// Lifts the first plan out of assistant text, or null.
    ///
    /// The first, not all: a reply containing two plans has changed its mind
    /// mid-sentence, and the later one is a revision rather than more steps.
    /// </summary>
    public static IReadOnlyList<string>? Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            var match = PlanBlockRegex.Match(text);
            if (!match.Success)
            {
                return null;
            }

            var body = match.Groups["body"].Value.Trim();
            if (body.Length == 0)
            {
                return null;
            }

            var steps = body.StartsWith('[')
                ? JsonSerializer.Deserialize<List<string>>(body)
                : ReadLines(body);

            var usable = (steps ?? [])
                .Select(step => step?.Trim() ?? string.Empty)
                .Where(step => step.Length > 0)
                .Take(MaxSteps)
                .ToList();

            return usable.Count > 0 ? usable : null;
        }
        catch (JsonException)
        {
            // A malformed plan is no plan. The run still happens and the tool
            // chips still show what it did — losing the summary is a smaller
            // cost than refusing the turn over a stray comma.
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// A model that ignores the JSON shape and writes a list instead. Accepted
    /// because being strict here buys nothing: the alternative to reading
    /// "- Read the config" is showing no plan at all.
    /// </summary>
    private static List<string> ReadLines(string body)
        => body.Split('\n')
            .Select(line => line.Trim().TrimStart('-', '*', '•', ' ').Trim())
            .Where(line => line.Length > 0)
            .Select(StripLeadingNumber)
            .ToList();

    private static string StripLeadingNumber(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
        {
            i++;
        }

        if (i == 0 || i >= line.Length)
        {
            return line;
        }

        return line[i] is '.' or ')' ? line[(i + 1)..].Trim() : line;
    }

    /// <summary>
    /// What to tell the model, added to the prompt only when tools are
    /// available and may run — a plan for work that cannot happen is noise.
    /// </summary>
    public static string SystemPromptAddendum =>
        """
        Before a task that will take several tool calls, state the plan first:

        ```plan
        ["First step", "Second step", "Third step"]
        ```

        Keep it to the steps you actually intend, in order, in plain language.
        A single-step task needs no plan.
        """;
}
