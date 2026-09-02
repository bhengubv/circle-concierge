using Concierge.Shared.Tools;

namespace Concierge.Shared.Presets;

/// <summary>
/// A named way to compose a session: which tools it can reach, who it sounds like, and what
/// it is permitted to do.
/// </summary>
/// <remarks>
/// The reason this exists rather than a set of flags: a restricted session should be built
/// smaller, not built full and then guarded. A child's session that never had the shell in
/// its catalogue cannot be talked into using one, and no prompt has to ask it not to.
/// </remarks>
public sealed class AgentPreset
{
    private readonly HashSet<string> _excludedTools;

    /// <param name="name">Short identifier, e.g. <c>kid</c>.</param>
    /// <param name="persona">System prompt fragment describing who the assistant is here.</param>
    /// <param name="permission">The permission level sessions on this preset run at.</param>
    /// <param name="excludedTools">
    /// Tools to withhold beyond what the permission level already withholds. Read-only is
    /// not the same as suitable for a child.
    /// </param>
    public AgentPreset(
        string name,
        string persona,
        ConciergePermissionPreset permission,
        IEnumerable<string>? excludedTools = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(persona);

        Name = name.Trim();
        Persona = persona;
        Permission = permission;
        _excludedTools = new HashSet<string>(excludedTools ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What this preset is called.</summary>
    public string Name { get; }

    /// <summary>Who the assistant is in sessions composed from this preset.</summary>
    public string Persona { get; }

    /// <summary>What sessions on this preset are permitted to do.</summary>
    public ConciergePermissionPreset Permission { get; }

    /// <summary>The permission policy this preset implies.</summary>
    public ConciergePermissionPolicy Policy => ConciergePermissionPolicy.For(Permission);

    /// <summary>Narrow a catalogue to what a session on this preset may reach.</summary>
    public IReadOnlyList<IAgentTool> SelectTools(IEnumerable<IAgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return Policy.SelectTools(tools).Where(tool => !_excludedTools.Contains(tool.Name)).ToList();
    }
}

/// <summary>The presets a deployment offers.</summary>
public sealed class AgentPresetRegistry
{
    private readonly Dictionary<string, AgentPreset> _presets;

    public AgentPresetRegistry(IEnumerable<AgentPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        _presets = presets.ToDictionary(preset => preset.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The presets Concierge ships with.
    /// </summary>
    /// <remarks>
    /// <c>kid</c> is read-only and additionally withholds search, because a child asking a
    /// question does not need to grep a filesystem and the result would be meaningless to
    /// them. <c>everyday</c> is the default adult session. <c>operator</c> is for someone who
    /// has accepted what full access means.
    /// </remarks>
    public static AgentPresetRegistry BuiltIn { get; } = new([
        new AgentPreset(
            "kid",
            "You are Bell. You are talking with a child. Use plain, warm words, keep answers short, "
            + "and never use a word you would not say to a nine-year-old at dinner.",
            ConciergePermissionPreset.ReadOnly,
            excludedTools: ["grep", "list_files"]),
        new AgentPreset(
            "everyday",
            "You are Bell. Be warm and plain-spoken. Ask before doing anything that changes a file.",
            ConciergePermissionPreset.WorkspaceWrite),
        new AgentPreset(
            "operator",
            "You are Bell, assisting an operator who has accepted full access. Be concise and exact.",
            ConciergePermissionPreset.FullAccess),
    ]);

    /// <summary>Every preset offered, by name.</summary>
    public IReadOnlyCollection<AgentPreset> All => _presets.Values;

    /// <summary>Find a preset, or null when the name is unknown.</summary>
    public AgentPreset? Find(string name)
        => !string.IsNullOrWhiteSpace(name) && _presets.TryGetValue(name.Trim(), out var preset) ? preset : null;
}
