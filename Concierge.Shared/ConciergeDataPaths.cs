namespace Concierge.Shared;

/// <summary>
/// One rooted home for Concierge's durable local data. Content is kept apart from
/// databases, service connections, secrets and model caches so it can be exported
/// without hunting through the operating system's media folders.
/// </summary>
public sealed class ConciergeDataPaths
{
    public ConciergeDataPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
            throw new ArgumentException("Concierge data needs an absolute folder.", nameof(root));
        Root = Path.GetFullPath(root);
    }

    public static ConciergeDataPaths Default { get; } = new(ResolveDefaultRoot());

    public string Root { get; }
    public string Content => Path.Combine(Root, "content");
    public string Music => Path.Combine(Content, "Music");
    public string Video => Path.Combine(Content, "Video");
    public string Pictures => Path.Combine(Content, "Pictures");
    public string Documents => Path.Combine(Content, "Documents");
    public string NotaDrafts => Path.Combine(Music, "Nota drafts");
    public string BookTrailers => Path.Combine(Video, "Book trailers");
    public string SleptOnPackages => Path.Combine(Content, "Exports", "SleptON");
    public string ConversationDatabase => Path.Combine(Root, "concierge.db");
    public string ProjectDatabase => Path.Combine(Root, "projects.db");

    /// <summary>
    /// Copy old Nota drafts into the consolidated content tree. Never delete or
    /// overwrite the source: migration must be safe to retry after interruption.
    /// </summary>
    public void CopyLegacyNotaDrafts()
    {
        var old = Path.Combine(Root, "Nota drafts");
        if (!Directory.Exists(old)) return;
        Directory.CreateDirectory(NotaDrafts);
        foreach (var path in Directory.EnumerateFiles(old))
        {
            var name = Path.GetFileName(path);
            if (!IsNotaDraft(name)) continue;
            var destination = Path.Combine(NotaDrafts, name);
            try { File.Copy(path, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { }
        }
    }

    private static bool IsNotaDraft(string name)
    {
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        return Path.GetExtension(name).ToLowerInvariant() is ".wav" or ".flac" or ".mp3";
    }

    private static string ResolveDefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(local)
            ? Path.Combine(Directory.GetCurrentDirectory(), ".concierge-artifacts")
            : local, "Concierge");
    }
}
