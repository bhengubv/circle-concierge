namespace Concierge.Shared;

/// <summary>
/// A named place a session works in. The host supplies it, so the trusted root is a decision
/// rather than a discovery.
/// </summary>
/// <remarks>
/// <para>
/// Every path guard in <see cref="AgentHarnessService"/> — containment, denied segments,
/// protected filenames, symlink rejection — is measured from this root. Locating it by
/// walking up for a solution file works on a developer's machine and produces the process's
/// current directory anywhere else, which is not a decision anyone made.
/// </para>
/// <para>
/// Each host names its own: the MAUI host passes app-scoped storage, the web host passes a
/// per-user folder, the CLI passes the repository it was started in.
/// </para>
/// </remarks>
public sealed class ConciergeWorkspace
{
    /// <param name="name">Human-readable label, shown when the user is asked to choose.</param>
    /// <param name="root">The directory the session may act inside. Created if absent.</param>
    public ConciergeWorkspace(string name, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Name = name.Trim();
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    /// <summary>What this working context is called.</summary>
    public string Name { get; }

    /// <summary>The absolute directory the session is confined to.</summary>
    public string Root { get; }
}
