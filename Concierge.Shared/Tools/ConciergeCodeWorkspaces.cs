namespace Concierge.Shared.Tools;

/// <summary>Stable workspace identifiers shared by Project navigation and the local Code host.</summary>
public static class ConciergeCodeWorkspaces
{
    public sealed record Kind(string Id, string Title, string Summary);

    public static IReadOnlyList<Kind> All { get; } =
    [
        new("code", "Code", "Build apps and edit source files."),
        new("games", "Games", "Prototype and play a game."),
        new("video", "Video", "Make a short animation."),
        new("audio", "Audio", "Compose and listen to sound."),
        new("images", "Images", "Create and edit visual assets."),
    ];

    public static Kind? Find(string? id) =>
        All.FirstOrDefault(kind => string.Equals(kind.Id, id, StringComparison.OrdinalIgnoreCase));
}
