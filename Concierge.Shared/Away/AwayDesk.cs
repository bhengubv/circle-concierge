using Concierge.Shared.Chat;
using Concierge.Shared.Design;
using Concierge.Shared.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Concierge.Shared.Away;

/// <summary>
/// The machine that does the making, answering a device that is not here.
///
/// **The canvas gets the sentence first**, exactly as the composer does. `DesignSpeech.Hear`
/// is pure and synchronous — a document in, a document out — so "make the title bigger" and
/// "change the look to night" work with no model, no network beyond the room, and nothing
/// loaded. That is Stage 1, and it is what makes a watch useful before any of the rest lands.
///
/// Anything the canvas cannot place falls through to <see cref="TurnRunner"/>, which is the
/// same turn the workspace runs. One loop, two callers.
/// </summary>
/// <remarks>
/// It reaches the design through <see cref="IDesignStore"/> — the file the canvas opens and
/// writes on every change — rather than into a running workspace, because the web head and
/// the desktop head are two programs and the file is what they already share.
///
/// **The cost, stated rather than found later: a canvas already open does not see this until
/// it is reopened.** It holds its own session in memory and writes over the file as it goes,
/// so a sentence from the wrist landing mid-edit would be overwritten by the next thing
/// typed. Making that live is a change to the canvas, and it should be made on purpose.
/// </remarks>
public sealed class AwayDesk(
    IDesignStore designs,
    TurnRunner turn,
    IConversationStore conversations,
    IEnumerable<IChatRuntime> runtimes,
    InteractiveToolApprovalService? approvals = null,
    RoomCatalogue? catalogue = null) : IAway
{
    /// <summary>
    /// Whose conversations these are. The same owner the workspace uses, so what was said
    /// on a wrist is in the thread list on the desk rather than in a second history nobody
    /// opens.
    /// </summary>
    private const string Owner = "local";

    /// <inheritdoc />
    public async Task<AwayAnswer> SayAsync(AwaySaid said, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(said.Text))
        {
            return new AwayAnswer(false, string.Empty, "Nothing was said.");
        }

        var restored = await designs.LoadAsync(cancellationToken).ConfigureAwait(false);

        // A design that cannot be read opens blank rather than refusing — the rule the canvas
        // already follows, because refusing leaves somebody with no way back to a working
        // surface and the bad file is still on disk either way.
        var document = restored.Document ?? DesignDocument.Blank();

        // Nothing is pointed at from a wrist: there is no canvas to touch, so the sentence
        // has to stand on its own. "Make it bigger" with no subject is not understood, and
        // saying so beats guessing which thing was meant.
        var heard = DesignSpeech.Hear(document, said.Text, pointedAt: null, catalogue);

        if (heard.Understood)
        {
            await designs.SaveAsync(heard.Document, cancellationToken).ConfigureAwait(false);

            return new AwayAnswer(true, heard.What, heard.What);
        }

        // The canvas often knows exactly what is wrong — "a page is one surface, say slides
        // first" — and on a face that sentence is the entire answer. Final means it is not
        // worth a model's turn.
        if (heard is { Final: true, Reply: { Length: > 0 } reply })
        {
            return new AwayAnswer(false, string.Empty, reply);
        }

        return await AskTheModelAsync(said, heard.Reply, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AwayAsk>> WaitingAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AwayAsk>>(approvals is null
            ? []
            : [.. approvals.Pending.Select(p => new AwayAsk(
                p.Id, p.Request.ToolName, p.Request.Summary, p.Request.Risk.ToString(), p.AskedAt))]);

    /// <inheritdoc />
    public Task<bool> AnswerAsync(Guid id, bool allowed, CancellationToken cancellationToken = default)
    {
        if (approvals is null)
        {
            return Task.FromResult(false);
        }

        // Answering something that is no longer waiting is not an error worth reporting as
        // one — a turn that timed out while somebody walked to the kitchen is ordinary. It
        // reports false so the face can say "that one has gone" rather than claiming it
        // allowed something.
        var waiting = approvals.Pending.Any(p => p.Id == id);

        if (waiting)
        {
            approvals.Answer(id, allowed ? ToolApprovalDecision.Allowed : ToolApprovalDecision.Denied);
        }

        return Task.FromResult(waiting);
    }

    /// <summary>
    /// Hand it to the model, with the situation it was said in.
    ///
    /// Where the sentence is answered by the canvas this never runs, which is the point: a
    /// watch on a train with no signal still changes a design.
    /// </summary>
    private async Task<AwayAnswer> AskTheModelAsync(
        AwaySaid said, string? fallback, CancellationToken cancellationToken)
    {
        var ready = runtimes.Where(r => r.IsReady && !string.Equals(r.Id, "null", StringComparison.Ordinal)).ToList();

        if (ready.FirstOrDefault() is not { } runtime)
        {
            // Nothing can answer, so the canvas's own guess is better than silence. That
            // reply was written for the composer hint and read by nobody for months.
            return new AwayAnswer(false, string.Empty, fallback ?? "Nothing here understood that.");
        }

        var conversation = await FindOrStartAsync(said.Context.Device, cancellationToken).ConfigureAwait(false);

        var result = await turn.RunAsync(
            new TurnRequest(
                conversation.Id,
                said.Text,
                runtime,
                ready,
                Settings: new TurnSettings(SystemPrompt: said.Context.AsSaid())),
            progress: null,
            cancellationToken).ConfigureAwait(false);

        // A face has room for a sentence, not a transcript. The last line of what came back
        // is the answer; the rest is in the thread on the desk, where there is room for it.
        var text = result.Text?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return new AwayAnswer(
            result.Text is { Length: > 0 },
            string.Empty,
            text ?? "Nothing came back.");
    }

    /// <summary>
    /// One thread per device, reused, so a week of things said into a watch is one
    /// conversation rather than a hundred one-line ones nobody can find.
    /// </summary>
    private async Task<Conversation> FindOrStartAsync(string device, CancellationToken cancellationToken)
    {
        var title = $"From the {device}";
        var existing = await conversations.ListAsync(Owner, cancellationToken).ConfigureAwait(false);

        return existing.FirstOrDefault(c => string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase))
            ?? await conversations.StartAsync(Owner, title, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

public static class AwayRegistration
{
    /// <summary>
    /// Lets a device that is not here reach the making.
    ///
    /// Registering this does not open anything — reaching it from outside the machine is a
    /// separate decision, made where the endpoint is.
    /// </summary>
    public static IServiceCollection AddConciergeAway(this IServiceCollection services)
    {
        services.TryAddScoped<IAway>(provider => new AwayDesk(
            provider.GetRequiredService<IDesignStore>(),
            provider.GetRequiredService<TurnRunner>(),
            provider.GetRequiredService<IConversationStore>(),
            provider.GetServices<IChatRuntime>(),
            provider.GetService<InteractiveToolApprovalService>(),
            provider.GetService<RoomCatalogue>()));

        return services;
    }
}
