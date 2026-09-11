using System.Text.Json.Nodes;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Devices;
using Concierge.Shared.Tools;

namespace Concierge.Devices;

/// <summary>
/// The two that reach another person: a message, and a call.
///
/// Held back until last on purpose. Every other capability touches the device;
/// these two touch somebody else, and a mistake is not undoable — you cannot
/// un-text a person, and the wrong number at two in the morning is a real thing
/// that happens to real people.
///
/// **The decision that makes them safe is that neither of them sends anything.**
/// `send_message` opens the messaging app with the message already written, and
/// `place_call` opens the dialler with the number already in it. A person presses
/// send. A person presses call. The model can get as far as putting the words in
/// front of you and no further.
///
/// That is worth being clear about, because the obvious build is the wrong one.
/// MAUI can send an SMS silently on Android with the right permission, and a
/// capability that did so would be one approval card away from a model that
/// misread a name texting the wrong person — with the approval card showing a
/// phone number that looks like any other phone number. Composing instead means
/// there are two gates and the second one is the operating system's own, in the
/// app somebody already recognises, showing the message as it will actually
/// arrive. Concierge does not get to skip that, and neither does anything that
/// talks it into trying.
///
/// So OpenDroid's payment actions stay absent, and these two are the furthest this
/// goes: prepare, and hand over.
///
/// Both are unavailable off a phone, like every other capability that cannot do
/// its job here — a desktop simply never hears of them.
///
/// The number check lives in <see cref="PhoneNumbers"/> in Shared rather than
/// here, because nothing in this project can be tested from a Windows machine and
/// that check is the part that has to be right.
/// </summary>
/// <summary>
/// A message, written and handed over unsent.
/// </summary>
internal sealed class ComposeMessageCapability : IDeviceCapability
{
    public string Name => "send_message";

    public string Description =>
        "Write a text message to a phone number and open it in the messaging app, ready to send. "
        + "It is not sent — the person reads it and presses send.";

    /// <summary>
    /// Said in the words somebody needs on the approval card. "Opens" and "does not
    /// send" are the two facts that decide whether to allow it, so they are the
    /// sentence rather than a footnote to it.
    /// </summary>
    public string Reach =>
        "It opens your messaging app with the message written out. Nothing is sent until you send it.";

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": {
            "to": { "type": "string", "description": "The phone number." },
            "message": { "type": "string", "description": "What it should say." }
          },
          "required": ["to", "message"] }
        """);

    public bool IsReadOnly => false;

    /// <summary>
    /// High, even though it sends nothing. What it does is put words in front of a
    /// person under their own name, and the risk on the card should match what a
    /// person would feel about it rather than what the code technically does.
    /// </summary>
    public ConciergeToolRisk Risk => ConciergeToolRisk.High;

    public bool Available
    {
        get
        {
            try
            {
                return Sms.Default.IsComposeSupported;
            }
            catch (Exception)
            {
                // A device that will not even answer the question cannot do it.
                // Absent beats a capability that throws when it is used.
                return false;
            }
        }
    }

    public async Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var to = arguments?["to"]?.GetValue<string>();
        var message = arguments?["message"]?.GetValue<string>();

        if (!PhoneNumbers.Dialable(to))
        {
            return new AgentToolResult(false, string.Empty, "That is not a phone number.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            // An empty message opened in somebody's messaging app is a puzzle, not
            // a message.
            return new AgentToolResult(false, string.Empty, "There is nothing to say in the message.");
        }

        try
        {
            await Sms.Default.ComposeAsync(new SmsMessage(message, [to!])).ConfigureAwait(false);

            return new AgentToolResult(
                true, $"Opened a message to {to} in your messaging app. It has not been sent.", null);
        }
        catch (FeatureNotSupportedException)
        {
            return new AgentToolResult(false, string.Empty, "This device cannot send messages.");
        }
    }
}

/// <summary>
/// A number, put in the dialler and left there.
/// </summary>
internal sealed class PlaceCallCapability : IDeviceCapability
{
    public string Name => "place_call";

    public string Description =>
        "Open the phone dialler with a number ready to call. It does not call — the person presses call.";

    public string Reach =>
        "It opens your dialler with the number in it. Nothing is dialled until you press call.";

    public JsonNode? ArgumentsSchema => JsonNode.Parse("""
        { "type": "object",
          "properties": { "number": { "type": "string", "description": "The phone number." } },
          "required": ["number"] }
        """);

    public bool IsReadOnly => false;

    public ConciergeToolRisk Risk => ConciergeToolRisk.High;

    public bool Available
    {
        get
        {
            try
            {
                return PhoneDialer.Default.IsSupported;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task<AgentToolResult> InvokeAsync(
        JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var number = arguments?["number"]?.GetValue<string>();

        if (!PhoneNumbers.Dialable(number))
        {
            return Task.FromResult(
                new AgentToolResult(false, string.Empty, "That is not a phone number."));
        }

        try
        {
            PhoneDialer.Default.Open(number!);

            return Task.FromResult(new AgentToolResult(
                true, $"Opened your dialler with {number}. It has not been called.", null));
        }
        catch (FeatureNotSupportedException)
        {
            return Task.FromResult(
                new AgentToolResult(false, string.Empty, "This device cannot place calls."));
        }
        catch (ArgumentNullException)
        {
            return Task.FromResult(
                new AgentToolResult(false, string.Empty, "That is not a phone number."));
        }
    }
}
