using System.Text.Json.Nodes;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Devices;

/// <summary>
/// Something Concierge can do to the device it is running on.
///
/// Every tool Concierge had before this worked on files, the web, or a notebook.
/// Nothing reached the machine underneath — no clipboard, no notification, no
/// "open this", no idea whether the battery was flat or the network was gone. On
/// a desktop that is a missing convenience. On a phone it is the whole product.
///
/// Why this is a separate interface from <see cref="IAgentTool"/> rather than
/// more tools: a tool exists or it does not, and the registry is built once in
/// shared code. A capability belongs to a *head*, and whether it works is a
/// question you can only ask on the device. Sending a text message is ordinary
/// on Android, impossible on Windows, and possible-but-not-wired on a watch. The
/// same binary answers differently depending on what it is running on, and
/// sometimes differently on the same device an hour later — a permission gets
/// revoked, a radio gets switched off.
///
/// So a capability carries three things a tool does not:
///
///   <see cref="Available"/> — asked at the moment the catalogue is built, not
///   assumed at compile time. What a device cannot do is left out rather than
///   offered and failed, because a model told a tool exists will keep trying it,
///   and a person watching that has no way to tell "broken" from "not here".
///
///   <see cref="Risk"/> — a capability that texts somebody is not the same kind
///   of thing as one that reads the battery level, and the person being asked to
///   approve it deserves to be told which.
///
///   <see cref="Reach"/> — one plain sentence about what it touches, in the words
///   a person would use. This is the sentence that appears above Allow and Deny.
/// </summary>
public interface IDeviceCapability
{
    /// <summary>Stable identifier, snake_case, as a tool name.</summary>
    string Name { get; }

    /// <summary>One line for the model.</summary>
    string Description { get; }

    /// <summary>
    /// One plain sentence for the person deciding, about what this reaches. Shown
    /// above Allow and Deny, so it says what happens rather than what it is called.
    /// </summary>
    string Reach { get; }

    /// <summary>Argument shape, or null for a capability that takes none.</summary>
    JsonNode? ArgumentsSchema { get; }

    /// <summary>
    /// True if this only observes. Reading the battery level changes nothing and
    /// should never interrupt anybody; everything else asks.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>How loud to be about it when asking.</summary>
    ConciergeToolRisk Risk { get; }

    /// <summary>
    /// Whether this device can do this, right now.
    ///
    /// Asked every time the catalogue is read, not cached at start-up: a
    /// permission can be granted or revoked while the app is open, and a radio
    /// can be switched off between one turn and the next.
    /// </summary>
    bool Available { get; }

    Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes this device's capabilities into the tool registry.
///
/// The same seam MCP uses, for the same reason: the registry reads its sources
/// every time rather than snapshotting them, so a capability that becomes
/// available after start-up — a permission finally granted — arrives without a
/// restart. That the registry works this way was itself a bug fix; before it,
/// MCP tools could never reach the model at all.
///
/// Two rules are enforced here rather than left to each head, because a head
/// that gets either wrong gets it wrong quietly:
///
///   Unavailable capabilities are not published. Not published-and-failing, not
///   published-with-a-warning — absent. The model's catalogue is a description of
///   what this device can do, and a description that lists things it cannot is
///   worse than a shorter one.
///
///   Everything that acts asks first. A head could forget; this cannot. It is the
///   same wrapper remote MCP tools get, and for a stronger reason — a capability
///   that sends a message or makes a call reaches a person who is not in the room.
/// </summary>
public sealed class DeviceCapabilitySource : IAgentToolSource
{
    private readonly IReadOnlyList<IDeviceCapability> _capabilities;
    private readonly IToolApprovalService _approval;

    public DeviceCapabilitySource(
        IEnumerable<IDeviceCapability> capabilities, IToolApprovalService approval)
    {
        _capabilities = (capabilities ?? throw new ArgumentNullException(nameof(capabilities))).ToList();
        _approval = approval ?? throw new ArgumentNullException(nameof(approval));
    }

    /// <summary>
    /// What this device can do, asked fresh. A capability that has gone away since
    /// the last turn is gone from the catalogue by the next one.
    /// </summary>
    public IReadOnlyList<IAgentTool> Tools =>
        _capabilities
            .Where(capability => IsAvailable(capability))
            .Select(capability => (IAgentTool)new ApprovedDeviceCapability(capability, _approval))
            .ToList();

    /// <summary>
    /// Everything this head declared, available or not, so a person can be shown
    /// what their device cannot do and why the assistant never offers it.
    ///
    /// The model is never given this list. It exists because "Concierge can't text
    /// from a laptop" is a reasonable thing to want explained, and silence explains
    /// nothing.
    /// </summary>
    public IReadOnlyList<(IDeviceCapability Capability, bool Available)> Declared =>
        _capabilities.Select(c => (c, IsAvailable(c))).ToList();

    /// <summary>
    /// A head's availability check runs on a real device and can throw for reasons
    /// nobody anticipated — a platform API that raises rather than returning false
    /// on an OS version nobody tested. Unavailable is the safe reading of "I don't
    /// know", and it costs a capability rather than the whole catalogue.
    /// </summary>
    private static bool IsAvailable(IDeviceCapability capability)
    {
        try
        {
            return capability.Available;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// A capability, wrapped so it asks before it acts.
///
/// The read-only ones pass straight through — interrupting somebody to read the
/// battery level would teach them to stop reading the prompts, which is the one
/// failure that makes every other approval worthless.
/// </summary>
internal sealed class ApprovedDeviceCapability : IAgentTool
{
    private readonly IDeviceCapability _capability;
    private readonly IToolApprovalService _approval;

    public ApprovedDeviceCapability(IDeviceCapability capability, IToolApprovalService approval)
    {
        _capability = capability;
        _approval = approval;
    }

    public string Name => _capability.Name;

    public string Description => _capability.Description;

    public JsonNode? ArgumentsSchema => _capability.ArgumentsSchema;

    public bool IsReadOnly => _capability.IsReadOnly;

    public async Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        if (_capability.IsReadOnly)
        {
            return await _capability.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        // Between the catalogue being built and the call being made, a permission
        // can be withdrawn. Saying so is better than asking somebody to approve
        // something that cannot happen.
        if (!_capability.Available)
        {
            return new AgentToolResult(false, string.Empty,
                $"This device can no longer do that ({_capability.Name}).");
        }

        var decision = await _approval.RequestAsync(
            new ToolApprovalRequest(_capability.Name, Summarise(arguments), _capability.Risk, _capability.Reach),
            cancellationToken).ConfigureAwait(false);

        if (decision != ToolApprovalDecision.Allowed)
        {
            return new AgentToolResult(false, string.Empty, "Not allowed, so nothing happened.");
        }

        return await _capability.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The title on the approval card: what is about to happen, with the arguments
    /// that make it specific. "Send a message" is not a decision anybody can make;
    /// "Send a message to +27…" is.
    /// </summary>
    private string Summarise(JsonNode? arguments)
    {
        if (arguments is not JsonObject values || values.Count == 0)
        {
            return _capability.Description;
        }

        var parts = values
            .Where(pair => pair.Value is not null)
            .Select(pair => $"{pair.Key}: {Shorten(pair.Value!.ToString())}")
            .ToList();

        return parts.Count == 0
            ? _capability.Description
            : $"{_capability.Name} — {string.Join(", ", parts)}";
    }

    /// <summary>
    /// An approval card is read at a glance, sometimes on a watch. A pasted essay
    /// as an argument must not push the buttons off the screen.
    /// </summary>
    private static string Shorten(string value)
    {
        var flat = value.ReplaceLineEndings(" ").Trim();

        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }
}
