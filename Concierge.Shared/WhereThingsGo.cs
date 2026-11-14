namespace Concierge.Shared;

/// <summary>
/// Durable creations stay in one Concierge content tree on every head. The
/// operating system's Music, Videos, Pictures and Documents folders are export
/// destinations chosen by a person, not implicit save locations.
/// </summary>
public static class WhereThingsGo
{
    public static string Music => ConciergeDataPaths.Default.Music;
    public static string Video => ConciergeDataPaths.Default.Video;
    public static string Pictures => ConciergeDataPaths.Default.Pictures;
    public static string Documents => ConciergeDataPaths.Default.Documents;
}
