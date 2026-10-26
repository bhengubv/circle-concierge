namespace Concierge.Shared.Settings;

/// <summary>Prevents two local app processes from replacing each other's saved JSON.</summary>
internal static class LocalStateWriteLease
{
    public static IDisposable Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try { return File.Open(path + ".write.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("This saved state is being updated by another Concierge window. Try again shortly.", ex); }
    }
}
