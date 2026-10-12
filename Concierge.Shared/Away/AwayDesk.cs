using System.Collections.Concurrent;
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
    Func<TurnRunner?> turn,
    IConversationStore conversations,
    IEnumerable<IChatRuntime> runtimes,
    InteractiveToolApprovalService? approvals = null,
    RoomCatalogue? catalogue = null,
    DesignWorkbench? workbench = null,
    AwayRequestLedger? requestLedger = null) : IAway
{
    private readonly AwayRequestLedger _requestLedger = requestLedger ?? new AwayRequestLedger();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeRuns = new();
    private readonly IChatRuntime[] _runtimes = runtimes.ToArray();
    /// <summary>
    /// Whose conversations these are. The same owner the workspace uses, so what was said
    /// on a wrist is in the thread list on the desk rather than in a second history nobody
    /// opens.
    /// </summary>
    private const string Owner = "local";

    /// <summary>
    /// What stood before the last change this made, and what that change was called.
    ///
    /// **Only needed when no canvas is open.** A canvas holds thirty moments and is the real
    /// history; this is the one step that exists when nothing else is remembering — a wrist
    /// on its own, changing a design file with no screen watching it. One step is a small
    /// claim, and the wrist is told the number rather than left to assume more.
    /// </summary>
    private DesignDocument? _before;

    private string _lastWhat = string.Empty;

    private DateTimeOffset _lastAt;

    public AwayCapabilities Capabilities
    {
        get
        {
            var circleAi = _runtimes.FirstOrDefault(runtime =>
                string.Equals(runtime.Id, "circleai", StringComparison.OrdinalIgnoreCase));
            var catalog = (circleAi as IChatRuntimeCapabilitySource)?.CapabilityCatalog;
            var canAnswer = _runtimes.Any(runtime => runtime.IsReady
                && !string.Equals(runtime.Id, "null", StringComparison.OrdinalIgnoreCase));
            return new AwayCapabilities(canAnswer, catalog);
        }
    }

    /// <inheritdoc />
    public async Task<AwayAnswer> SayAsync(AwaySaid said, CancellationToken cancellationToken = default)
    {
        if (said.RequestId is not { } requestId)
            return await SayCoreAsync(said, cancellationToken).ConfigureAwait(false);

        var candidateCancellation = new CancellationTokenSource();
        var runCancellation = _activeRuns.GetOrAdd(requestId, candidateCancellation);
        var ownsCancellation = ReferenceEquals(runCancellation, candidateCancellation);
        if (!ownsCancellation)
            candidateCancellation.Dispose();
        try
        {
            return await _requestLedger.ExecuteOnceAsync(requestId, AwayRequestLedger.Fingerprint(said),
                // The phone's HTTP connection may disappear after acceptance. The run keeps
                // going unless an explicit cancel arrives with this same durable ID.
                () => SayCoreAsync(said, runCancellation.Token), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsCancellation)
            {
                if (_activeRuns.TryGetValue(requestId, out var current) && ReferenceEquals(current, runCancellation))
                    _activeRuns.TryRemove(requestId, out _);
                runCancellation.Dispose();
            }
        }
    }

    public Task<bool> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (!_activeRuns.TryGetValue(runId, out var stopping))
            return Task.FromResult(false);
        try
        {
            stopping.Cancel();
            return Task.FromResult(true);
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(false);
        }
    }

    private async Task<AwayAnswer> SayCoreAsync(AwaySaid said, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(said.Text))
        {
            return new AwayAnswer(false, string.Empty, "Nothing was said.");
        }

        if (said.ProjectId == Guid.Empty || said.ProjectWorkspaceId == Guid.Empty
            || said.ProjectWorkspaceId is not null && said.ProjectId is null
            || said.ProjectId is null && !string.IsNullOrWhiteSpace(said.ProjectContext)
            || said.ProjectContext is { Length: > 16_384 })
        {
            return new AwayAnswer(false, string.Empty, "That project context is invalid or too large.", said.ConversationId);
        }

        // A client-created ID makes this the same durable conversation on both devices.
        // The phone has already recorded its local copy; this side creates its own copy
        // under that exact ID before the shared turn is run.
        if (said.ConversationId is { } sharedId)
        {
            var existing = await conversations.GetAsync(sharedId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                await conversations.StartWithIdAsync(sharedId, Owner, cancellationToken: cancellationToken).ConfigureAwait(false);
            else if (!string.Equals(existing.OwnerId, Owner, StringComparison.Ordinal))
                return new AwayAnswer(false, string.Empty, "That conversation is not available on this Desktop.", sharedId);

            if (said.ConversationEvents is { Count: > 0 } snapshot)
                await conversations.SynchronizeEventsAsync(sharedId, snapshot, cancellationToken).ConfigureAwait(false);
        }

        // **A picture always goes to the model.** `DesignSpeech` is a document in and a
        // document out and can carry no bytes at all, so letting the canvas answer a sentence
        // that arrived with a photograph would drop the photograph in silence — the defect
        // this repository exists to remove, on the one road a camera has into the product.
        if (said.Picture is not null)
        {
            return await AskTheModelAsync(said, null, cancellationToken).ConfigureAwait(false);
        }

        // Plan only must not take the canvas fast path: DesignSpeech applies changes
        // directly and bypasses TurnRunner's ExecuteTools gate.
        if (said.PermissionMode == ToolPermissionMode.PlanOnly)
            return await AskTheModelAsync(said, null, cancellationToken).ConfigureAwait(false);

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
            // Kept before it is written over, so "no, not like that" has something to go back
            // to. Only meaningful with no canvas open; with one, its own moments are deeper
            // and this is never read.
            _before = document;
            _lastWhat = heard.What;
            _lastAt = DateTimeOffset.UtcNow;

            await designs.SaveAsync(heard.Document, cancellationToken).ConfigureAwait(false);

            if (said.ConversationId is { } directId)
                await RecordQuickReplyAsync(said, directId, heard.What, "Circle canvas", cancellationToken).ConfigureAwait(false);

            return new AwayAnswer(true, heard.What, heard.What, said.ConversationId);
        }

        // The canvas often knows exactly what is wrong — "a page is one surface, say slides
        // first" — and on a face that sentence is the entire answer. Final means it is not
        // worth a model's turn.
        if (heard is { Final: true, Reply: { Length: > 0 } reply })
        {
            if (said.ConversationId is { } directId)
                await RecordQuickReplyAsync(said, directId, reply, "Circle canvas", cancellationToken).ConfigureAwait(false);
            return new AwayAnswer(false, string.Empty, reply, said.ConversationId);
        }

        return await AskTheModelAsync(said, heard.Reply, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ConversationEvent>> ReadConversationEventsAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => conversations.ReadEventsAsync(conversationId, cancellationToken);

    public Task<IReadOnlyList<ConversationEvent>> ReadConversationEventsAfterAsync(
        Guid conversationId, int afterSeq, CancellationToken cancellationToken = default)
        => conversations.ReadEventsAfterAsync(conversationId, afterSeq, cancellationToken);

    public Task<IReadOnlyList<ConversationEvent>> ReadConversationEventsPageAsync(
        Guid conversationId, int afterSeq, int pageSize, CancellationToken cancellationToken = default)
        => conversations.ReadEventsPageAsync(conversationId, afterSeq, pageSize, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> SynchronizeConversationEventsAsync(
        Guid conversationId,
        IReadOnlyList<ConversationEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (conversationId == Guid.Empty || events.Count > ConversationEventPage.MaximumSize)
            return false;
        if (events.Count == 0)
            return true;
        if (events.Any(entry => entry is null || entry.ConversationId != conversationId || entry.Id == Guid.Empty))
            return false;

        var existing = await conversations.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            // A handset creates the conversation identity before it can sync. Reusing that
            // identity means a later Desktop turn continues this exact event log.
            await conversations.StartWithIdAsync(conversationId, Owner, "From Mobile", cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else if (!string.Equals(existing.OwnerId, Owner, StringComparison.Ordinal))
        {
            return false;
        }

        await conversations.SynchronizeEventsAsync(conversationId, events, cancellationToken).ConfigureAwait(false);
        return true;
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

    /// <inheritdoc />
    public Task<AwayChange?> LastChangeAsync(CancellationToken cancellationToken = default)
    {
        // A canvas open on this machine is the real history — the same thirty moments the
        // strip shows — so the wrist and the screen never disagree about what just happened.
        if (workbench?.Session is { } session)
        {
            var moment = session.Moments[session.Position];

            return Task.FromResult<AwayChange?>(
                new AwayChange(moment.What, session.CanGoBack, session.Position, moment.At));
        }

        return Task.FromResult(_before is null
            ? null
            : new AwayChange(_lastWhat, true, 1, _lastAt));
    }

    /// <inheritdoc />
    public async Task<AwayChange?> UndoAsync(CancellationToken cancellationToken = default)
    {
        if (workbench?.Session is { } session)
        {
            if (!session.CanGoBack)
            {
                return null;
            }

            session.Back();

            // Written out as well as stepped back, because the file is what every other head
            // reads. A canvas that went back on screen and left the old document on disk
            // would be two answers to "what does this design say".
            await designs.SaveAsync(session.Current, cancellationToken).ConfigureAwait(false);

            var moment = session.Moments[session.Position];

            return new AwayChange(moment.What, session.CanGoBack, session.Position, moment.At);
        }

        if (_before is null)
        {
            return null;
        }

        await designs.SaveAsync(_before, cancellationToken).ConfigureAwait(false);

        // One step is all there is here, and once it is taken there is no second one. Said by
        // handing back CanUndo false rather than by leaving a button that does nothing.
        _before = null;

        return new AwayChange("Back as you left it", false, 0, DateTimeOffset.UtcNow);
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
        var ready = _runtimes.Where(r => r.IsReady && !string.Equals(r.Id, "null", StringComparison.Ordinal)).ToList();

        // Something that can see goes first when there is something to see. Asked of the
        // runtime's own list rather than of the interface: the local runtime declares the
        // capability because it *can* see, and lists nothing while a text-only model is
        // loaded — so checking the interface alone sends a photograph to a Qwen model and
        // says nothing about it.
        if (said.Picture is not null)
        {
            ready = [.. ready.OrderByDescending(r =>
                r is IVisionCapableRuntime { SupportedImageMediaTypes.Count: > 0 })];
        }

        if (ready.FirstOrDefault() is not { } runtime)
        {
            // Nothing can answer, so the canvas's own guess is better than silence. That
            // reply was written for the composer hint and read by nobody for months.
            var unavailable = fallback ?? "Nothing here understood that.";
            if (said.ConversationId is { } directId)
                await RecordQuickReplyAsync(said, directId, unavailable, "Desktop fallback", cancellationToken).ConfigureAwait(false);
            return new AwayAnswer(false, string.Empty, unavailable, said.ConversationId);
        }

        var conversation = said.ConversationId is { } sharedId
            ? (await conversations.GetAsync(sharedId, cancellationToken).ConfigureAwait(false))!
            : await FindOrStartAsync(said.Context.Device, cancellationToken).ConfigureAwait(false);

        // A picture handed to something that cannot look at it is worse than no picture: the
        // reply discusses it as though it had been seen. Said plainly instead, and the
        // sentence still goes — somebody who photographs a wall and asks about it deserves an
        // answer about the wall or an honest "I cannot see it", never a confident guess.
        var blind = said.Picture is not null
            && runtime is not IVisionCapableRuntime { SupportedImageMediaTypes.Count: > 0 };

        var text = blind
            ? said.Text
              + $"\n\n[{said.Picture!.FileName} was sent, but {runtime.EngineLabel} cannot look at pictures.]"
            : said.Text;

        // The runner is asked for per turn rather than held.
        //
        // **It is scoped and this is not**, which is a real difference rather than a detail:
        // the repeat reminder inside it is per-conversation on purpose, so one runaway loop is
        // not remembered against the next. Holding one here would have kept a single reminder
        // for the life of the process — and, as DI pointed out by refusing to start, a
        // singleton cannot hold a scoped service at all.
        //
        // Found by running it. Every test built this class directly and never once went
        // through a container, so the first thing to notice was a 500 on the first request.
        if (turn() is not { } runner)
        {
            var unavailable = fallback ?? "Nothing here can answer that.";
            if (said.ConversationId is not null)
                await RecordQuickReplyAsync(said, conversation.Id, unavailable, "Desktop fallback", cancellationToken).ConfigureAwait(false);
            return new AwayAnswer(false, string.Empty, unavailable, conversation.Id);
        }

        var permissionMode = said.PermissionMode ?? ToolPermissionMode.AskFirst;
        using var approvalMode = approvals?.ForTurn(permissionMode == ToolPermissionMode.ActFreely);
        var result = await runner.RunAsync(
            new TurnRequest(
                conversation.Id,
                text,
                runtime,
                ready,
                Images: said.Picture is not null && !blind
                    ? [new ChatImage(said.Picture.FileName, said.Picture.MediaType, said.Picture.Bytes)]
                    : null,
                Settings: new TurnSettings(
                    ExecuteTools: permissionMode != ToolPermissionMode.PlanOnly,
                    IncludeToolCatalog: said.IncludeToolCatalog,
                    SystemPrompt: string.Join("\n\n", new[] { said.Context.AsSaid(), said.ProjectContext }
                        .Where(value => !string.IsNullOrWhiteSpace(value))),
                    PermissionMode: permissionMode),
                InputAlreadyRecorded: said.ConversationEvents is { Count: > 0 },
                RunId: said.RequestId,
                ProjectId: said.ProjectId,
                ProjectWorkspaceId: said.ProjectWorkspaceId),
            progress: null,
            cancellationToken).ConfigureAwait(false);

        // A face has room for a sentence, not a transcript. The last line of what came back
        // is the answer; the rest is in the thread on the desk, where there is room for it.
        var answer = said.ProjectId is not null
            ? result.Text
            : result.Text?
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();

        return new AwayAnswer(
            result.Text is { Length: > 0 },
            string.Empty,
            answer ?? "Nothing came back.",
            conversation.Id);
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

    private async Task RecordQuickReplyAsync(AwaySaid said, Guid conversationId, string reply,
        string producedBy, CancellationToken cancellationToken)
    {
        // Canvas commands do not enter TurnRunner, so record them here to keep the
        // Desktop and phone transcripts continuous under the shared conversation ID.
        var promptIsRecorded = said.ConversationEvents?.Any(entry => entry.Type == ConversationEventType.UserMessage
            && string.Equals(entry.Data, said.Text, StringComparison.Ordinal)) == true;
        if (!promptIsRecorded)
            await conversations.AppendEventAsync(conversationId, ConversationEventType.UserMessage,
                said.Text, said.Context.Device, cancellationToken).ConfigureAwait(false);
        await conversations.AppendEventAsync(conversationId, ConversationEventType.AssistantMessage,
            reply, producedBy, cancellationToken).ConfigureAwait(false);
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
        // Singleton rather than scoped: the one step it remembers with no canvas open has to
        // outlive the request that made it, or "no, not like that" has nothing to go back to
        // the moment the connection closes.
        services.TryAddSingleton<IAway>(provider => new AwayDesk(
            provider.GetRequiredService<IDesignStore>(),
            // A scope per turn, so the scoped pieces inside the runner — the repeat reminder
            // above all — are per-turn rather than per-process.
            () => provider.GetRequiredService<IServiceScopeFactory>()
                .CreateScope().ServiceProvider.GetService<TurnRunner>(),
            provider.GetRequiredService<IConversationStore>(),
            provider.GetServices<IChatRuntime>(),
            provider.GetService<InteractiveToolApprovalService>(),
            provider.GetService<RoomCatalogue>(),
            provider.GetService<DesignWorkbench>()));

        return services;
    }
}
