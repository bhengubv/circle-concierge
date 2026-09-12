using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Design;

/// <summary>
/// Bringing what somebody already has into a design.
///
/// **Nothing could.** A creator arrives with forty photographs, a folder of stems and a
/// manuscript, and the only door into this surface was a paperclip that takes one picture at
/// a time. Every other capability is worth more once their existing work is inside, which is
/// why this comes before anything else on the list.
///
/// Said rather than browsed — "bring in the music from my Music folder" — because the verb on
/// this surface is speech. A file dialog is a thing you need a mouse and a desk for, and the
/// point of the product is that you do not.
/// </summary>
/// <remarks>
/// **What travels and what points.** A picture is carried as a data URI, so the design is
/// genuinely portable — the same rule the paperclip already follows. Audio and video are
/// referenced by path, the same rule `design_add_footage` already follows, because a folder
/// of albums is gigabytes and `design.json` is rewritten whenever anybody edits a heading.
///
/// So a design holding music points at this machine and is not portable the way one holding
/// pictures is. That is said here rather than left to be discovered, and the renderer already
/// draws an unplayable track as a named placeholder rather than a dead player.
/// </remarks>
public sealed class BringItIn(DesignWorkbench workbench) : IAgentTool
{
    /// <summary>
    /// How many things one sentence may bring in.
    ///
    /// A folder can hold ten thousand files. Somebody who says "bring in my pictures" and
    /// gets ten thousand nodes has not been helped — the canvas is unusable, the save is
    /// enormous, and undoing it is one step that takes a minute. Bounded, and it says what it
    /// left behind rather than silently stopping.
    /// </summary>
    public const int Most = 40;

    /// <summary>
    /// The largest picture carried into a design, matching the paperclip's own cap.
    ///
    /// A base64 picture is a third larger than the file and lives in every render of the page
    /// from here on, so this is a real cost rather than a formality.
    /// </summary>
    public const long BiggestPicture = 4 * 1024 * 1024;

    private static readonly string[] Pictures = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];
    private static readonly string[] Sounds = [".mp3", ".m4a", ".wav", ".flac", ".aac", ".ogg", ".opus"];
    private static readonly string[] Films = [".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v"];

    public string Name => "design_bring_in";

    public string Description =>
        "Bring what is already on this machine into the design — the pictures, music or video "
        + "in a folder. Say which folder. Pictures travel inside the design; music and video "
        + "are pointed at where they are.";

    public JsonNode? ArgumentsSchema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["folder"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The folder to bring things in from.",
            },
            ["what"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "pictures, music, video, or everything. Everything by default.",
            },
        },
        ["required"] = new JsonArray("folder"),
    };

    /// <summary>
    /// It changes the canvas, and going back is free — the same rule every other design tool
    /// follows. It reads files rather than writing any, and reaches nothing off this machine.
    /// </summary>
    public bool IsReadOnly => false;

    public async Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        if (workbench.Session is not { } session)
        {
            return new AgentToolResult(false, string.Empty, "There is no canvas open.");
        }

        var folder = (arguments?["folder"]?.GetValue<string>() ?? string.Empty).Trim();

        if (folder.Length == 0)
        {
            return new AgentToolResult(false, string.Empty, "Say which folder to bring things in from.");
        }

        bool there;

        try
        {
            there = Directory.Exists(folder);
        }
        catch (ArgumentException)
        {
            there = false;
        }

        if (!there)
        {
            return new AgentToolResult(false, string.Empty, $"There is no folder at {folder}.");
        }

        var want = (arguments?["what"]?.GetValue<string>() ?? "everything").Trim().ToLowerInvariant();

        var wanted = want switch
        {
            "pictures" or "picture" or "photos" or "photo" or "images" or "art" => Pictures,
            "music" or "songs" or "tracks" or "audio" or "sound" or "sounds" => Sounds,
            "video" or "videos" or "footage" or "films" or "clips" => Films,
            _ => [.. Pictures, .. Sounds, .. Films],
        };

        IReadOnlyList<string> found;

        try
        {
            // Top level only. Walking a whole drive because somebody said "my pictures" is the
            // kind of helpfulness nobody asked for, and it is slow in exactly the case where
            // the folder was the wrong one.
            found = Directory.EnumerateFiles(folder)
                .Where(file => wanted.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return new AgentToolResult(false, string.Empty, $"That folder could not be read: {failure.Message}");
        }

        if (found.Count == 0)
        {
            return new AgentToolResult(
                false, string.Empty, $"There is nothing to bring in from {folder}.");
        }

        var document = session.Current;
        var brought = 0;
        var tooBig = 0;
        var unreadable = 0;

        foreach (var file in found.Take(Most))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(file);
            var name = Path.GetFileNameWithoutExtension(file);

            if (Sounds.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                document = document.Add(DesignNode.New(
                    DesignNodeKind.Sound, null, ("src", file), ("text", name)));
                brought++;
                continue;
            }

            if (Films.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                document = document.Add(DesignNode.New(
                    DesignNodeKind.Frame, null, ("src", file), ("text", name)));
                brought++;
                continue;
            }

            try
            {
                var size = new FileInfo(file).Length;

                if (size > BiggestPicture)
                {
                    tooBig++;
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);

                // The kind is read from the bytes, not the extension — the same helper the
                // paperclip and the vision path use. A file called holiday.png that is not a
                // PNG would otherwise become a data URI claiming to be one.
                if (Attachments.AttachmentKind.ImageMediaType(bytes) is not { } kind)
                {
                    unreadable++;
                    continue;
                }

                document = document.Add(DesignNode.New(
                    DesignNodeKind.Image,
                    null,
                    ("src", $"data:{kind};base64,{Convert.ToBase64String(bytes)}"),
                    ("text", name)));

                brought++;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        if (brought == 0)
        {
            return new AgentToolResult(
                false,
                string.Empty,
                Left(found.Count, 0, tooBig, unreadable) is { Length: > 0 } why
                    ? $"Nothing could be brought in. {why}"
                    : "Nothing could be brought in.");
        }

        session.Record(document, $"Brought in {brought}");

        var note = Left(found.Count, brought, tooBig, unreadable);

        return new AgentToolResult(
            true,
            note.Length > 0 ? $"Brought in {brought}. {note}" : $"Brought in {brought}.");
    }

    /// <summary>
    /// What was left behind, and why.
    ///
    /// **Said, always.** A folder of sixty photographs that quietly becomes forty is the
    /// defect this repository has spent its whole history removing: a screen that looks like
    /// it worked. Somebody who is missing twenty pictures needs to know that now, not when
    /// they publish.
    /// </summary>
    private static string Left(int found, int brought, int tooBig, int unreadable)
    {
        var notes = new List<string>();

        if (found > Most)
        {
            notes.Add($"{found - Most} more are in there — say it again to bring in the next lot");
        }

        if (tooBig > 0)
        {
            notes.Add($"{tooBig} too big to carry (pictures over {BiggestPicture / (1024 * 1024)} MB)");
        }

        if (unreadable > 0)
        {
            notes.Add($"{unreadable} could not be read");
        }

        return notes.Count == 0 ? string.Empty : string.Join("; ", notes) + ".";
    }
}
