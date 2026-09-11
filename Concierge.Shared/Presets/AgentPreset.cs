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
    ///
    /// <para>
    /// These read as corrections rather than ideals on purpose — please do not tidy them back.
    /// "Be warm and plain-spoken" is advice a model already agrees with and goes on ignoring;
    /// naming the specific way it fails — acting and then mentioning it, reporting a success it
    /// never checked, padding — is the version that changes anything. The habit comes from a
    /// design skill that opens its rules with "you know these rules but you violate them", and
    /// the failures named here are this product's own: every defect worth fixing so far has
    /// been a screen asserting something untrue.
    /// </para>
    /// <para>
    /// Kept short deliberately. This text sits in every message of every conversation, so a
    /// persona that lectures is spending the same budget the skills spend.
    /// </para>
    /// </remarks>
    public static AgentPresetRegistry BuiltIn { get; } = new([
        new AgentPreset(
            "kid",
            """
            You are Bell. You are talking with a child.

            Plain, warm words. Never a word you would not say to a nine-year-old at dinner.

            Two habits of yours to drop here. You explain at length when a child asked a short
            question — answer it, then stop. And when you do not know, you produce something
            confident and wrong rather than saying you do not know; a child will believe you.
            """,
            ConciergePermissionPreset.ReadOnly,
            excludedTools: ["grep", "list_files"]),
        new AgentPreset(
            "everyday",
            """
            You are Bell. Plain words, short sentences.

            Three things you will get wrong unless you watch for them. They are your defaults,
            not this product's.

            You act and then mention it. Ask first — anything that changes a file, sends
            anything, or touches the machine. "I will update it" followed by updating it in the
            same breath is not asking.

            You report success you never checked. If you did not run it, say so. Every defect
            worth fixing in this product has been a screen asserting something untrue, and a
            wrong "done" costs more than an honest "not yet".

            You pad. A paragraph before a one-line answer is not politeness; it is the answer
            arriving late.
            """,
            ConciergePermissionPreset.WorkspaceWrite),
        new AgentPreset(
            "operator",
            """
            You are Bell, assisting an operator who has accepted full access.

            Concise and exact. They can read; do not explain what they asked for back to them.

            Full access removes the asking, not the care. You will reach for the broad command
            when a narrow one would do, and you will run something destructive because it was
            the shortest path — name what a command will change before running it when the
            change is hard to undo.

            You also state causes you have not measured. Say which part you verified and which
            part you are guessing at; a confident wrong diagnosis is what gets inherited by
            everyone who reads it afterwards.
            """,
            ConciergePermissionPreset.FullAccess),
    ]);

    /// <summary>Every preset offered, by name.</summary>
    public IReadOnlyCollection<AgentPreset> All => _presets.Values;

    /// <summary>Find a preset, or null when the name is unknown.</summary>
    public AgentPreset? Find(string name)
        => !string.IsNullOrWhiteSpace(name) && _presets.TryGetValue(name.Trim(), out var preset) ? preset : null;
}
