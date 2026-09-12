using System.Text;
using Concierge.Shared.Context;
using Concierge.Shared.Media;
using Concierge.Shared.Skills;
using Concierge.Shared.Tools;

namespace Concierge.Shared.Chat;

/// <summary>Which part of a turn a <see cref="TurnStep"/> is reporting.</summary>
public enum TurnStage
{
    /// <summary>More of the reply has arrived. <see cref="TurnStep.Text"/> is everything so far.</summary>
    Streaming,

    /// <summary>Something was written to the log, so a surface showing it should re-read.</summary>
    Recorded,

    /// <summary>A new round of the tool loop started.</summary>
    Round,

    /// <summary>The plan was stated or revised.</summary>
    Plan,

    /// <summary>Somebody answered, possibly after falling back past others.</summary>
    Answered,
}

/// <summary>One thing that happened during a turn, for a surface that is watching.</summary>
public sealed record TurnStep(
    TurnStage Stage,
    string? Text = null,
    int Iteration = 0,
    IReadOnlyList<string>? PlanSteps = null,
    int PlanDone = 0,
    bool PlanRevised = false,
    FailoverOutcome? Answered = null);

/// <summary>How a turn should be run.</summary>
/// <param name="ExecuteTools">
/// Whether tool calls in the reply are actually run. False is Plan only — the model still
/// sees the catalogue and can say what it would do.
/// </param>
/// <param name="IncludeToolCatalog">Whether the catalogue goes in the system prompt at all.</param>
/// <param name="SkillIds">Skills switched on for this turn.</param>
/// <param name="SystemPrompt">The conversation's own system text, when it has one.</param>
public sealed record TurnSettings(
    bool ExecuteTools = true,
    bool IncludeToolCatalog = true,
    IReadOnlyList<string>? SkillIds = null,
    string? SystemPrompt = null);

/// <summary>A turn to run.</summary>
/// <param name="ConversationId">Which conversation it belongs to.</param>
/// <param name="Text">
/// What to say, already composed. Attachment blocks and anything else a surface folds in
/// are the surface's business; by here it is one prompt.
/// </param>
/// <param name="Runtime">Who to ask first.</param>
/// <param name="Alternatives">Who may be fallen back to, in the order the caller holds them.</param>
/// <param name="Images">Pictures riding on this turn.</param>
public sealed record TurnRequest(
    Guid ConversationId,
    string Text,
    IChatRuntime Runtime,
    IReadOnlyList<IChatRuntime>? Alternatives = null,
    IReadOnlyList<ChatImage>? Images = null,
    TurnSettings? Settings = null);

/// <summary>What a turn came to.</summary>
public sealed record TurnResult(
    string? Text,
    string? AnsweredBy,
    IReadOnlyList<string> PlanSteps,
    int PlanDone,
    string? Stopped = null);

/// <summary>
/// Running one turn: ask, read the reply, run whatever it called for, ask again.
///
/// **Lifted out of <c>WorkspaceBase.SendAsync</c>**, where it was about three hundred and
/// seventy lines tangled with render state inside a two-and-a-half-thousand-line component.
/// Nothing about a turn needs a browser, and everything that does need one — the streaming
/// buffer, the plan strip, the moments — is a surface reading <see cref="TurnStep"/>.
///
/// **Why it had to move rather than be copied.** A watch has no component to render into,
/// and the honest way to give it the product is one loop with two callers. A second loop in
/// an endpoint would be a second answer to the most important question here — what may run,
/// in what order, credited to whom — and the two would drift. This repository has spent its
/// history removing exactly that shape.
/// </summary>
public sealed class TurnRunner(
    IConversationStore store,
    IAgentToolRegistry tools,
    IToolCallScheduler scheduler,
    ICompactionEngine compaction,
    IToolResultPruner pruner,
    IRepeatToolReminder repeats,
    ISkillRuntime skills,
    ConciergeToolLoopOptions options,
    BackgroundRuns runs,
    CapturedImages captured,
    RuntimeResolver? resolver = null)
{
    private readonly RuntimeResolver _resolver = resolver ?? new RuntimeResolver();

    /// <summary>
    /// The plan for the turn in flight. Live state, like the strip that shows it — a plan
    /// describes a run, and a run does not survive a restart either.
    /// </summary>
    private readonly PlanProgress _plan = new();

    /// <summary>
    /// Run a turn to completion.
    ///
    /// The user's words are written to the log here rather than by the caller, so a surface
    /// and an endpoint record a question the same way — the transcript is the one place a
    /// turn can be reconstructed from, and two ways of starting one is two shapes of history.
    /// </summary>
    public async Task<TurnResult> RunAsync(
        TurnRequest request,
        IProgress<TurnStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = request.Settings ?? new TurnSettings();
        var images = request.Images is { Count: > 0 } given ? given.ToList() : [];

        await store.AppendEventAsync(
            request.ConversationId, ConversationEventType.UserMessage, request.Text, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new TurnStep(TurnStage.Recorded));

        _plan.Clear();

        FailoverOutcome? answeredBy = null;
        string? last = null;
        string? stopped = null;

        runs.Started(request.ConversationId, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));

        try
        {
            // The outer loop drives the tool-call cycle: stream, persist, and if the reply
            // called for tools and the caller allows it, run them, write the results in as a
            // turn of their own, and stream again. Capped so a reply that keeps calling the
            // same thing cannot pin the conversation.
            for (var iteration = 0; iteration <= options.MaxToolIterations; iteration++)
            {
                progress?.Report(new TurnStep(TurnStage.Round, Iteration: iteration));

                var streamed = await StreamOnceAsync(
                    request, settings, images, outcome =>
                    {
                        answeredBy = outcome;
                        progress?.Report(new TurnStep(TurnStage.Answered, Answered: outcome));
                    },
                    progress, cancellationToken).ConfigureAwait(false);

                // Pictures ride exactly one turn. A filmstrip attached to every later
                // question is worse than no filmstrip at all.
                images.Clear();

                if (streamed is null)
                {
                    break;
                }

                last = streamed;

                // Stated before the work, so it can be read before anything has happened
                // rather than reconstructed afterwards from what did.
                if (PlanProtocol.Extract(streamed) is { Count: > 0 } stated)
                {
                    var revised = _plan.State(stated);

                    if (revised)
                    {
                        await store.AppendEventAsync(
                            request.ConversationId, ConversationEventType.ToolResult,
                            $"The plan was revised ({_plan.Revisions} so far this turn).",
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }

                    progress?.Report(new TurnStep(
                        TurnStage.Plan, PlanSteps: _plan.Steps, PlanDone: _plan.Done, PlanRevised: revised));
                }

                if (!settings.ExecuteTools || tools.Tools.Count == 0)
                {
                    break;
                }

                var calls = ToolCallProtocol.Extract(streamed);

                if (calls.Count == 0)
                {
                    break;
                }

                if (iteration == options.MaxToolIterations)
                {
                    stopped = $"The tool loop stopped after {options.MaxToolIterations} rounds without finishing.";

                    await store.AppendEventAsync(
                        request.ConversationId, ConversationEventType.ToolResult, stopped,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }

                // Each call is recorded before it runs, so a turn abandoned half way still
                // leaves a log that says what was asked for.
                foreach (var call in calls)
                {
                    await store.AppendEventAsync(
                        request.ConversationId, ConversationEventType.ToolCall,
                        call.Name + " " + (call.Arguments?.ToJsonString() ?? "{}"),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                var planned = calls.Select(call => new PlannedToolCall(call.Name, call.Arguments)).ToList();
                var outcomes = await scheduler.ExecuteAsync(planned, cancellationToken).ConfigureAwait(false);

                // One round of calls is one step done — but only a round where something
                // actually worked. Ticking regardless is the strip asserting progress
                // nobody made, in the place a person looks to decide whether to let it run on.
                var verdict = _plan.Round(outcomes.Select(o => o.Result.Success).ToList());

                foreach (var outcome in outcomes)
                {
                    // Oversized output is trimmed once here rather than paid for on every
                    // later request in this conversation.
                    var trimmed = pruner.Prune(outcome.Result.Output);
                    var result = trimmed.WasPruned
                        ? new AgentToolResult(outcome.Result.Success, trimmed.Text, outcome.Result.FailureMessage)
                        : outcome.Result;

                    await store.AppendEventAsync(
                        request.ConversationId, ConversationEventType.ToolResult,
                        ToolCallProtocol.FormatResult(outcome.ToolName, result),
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    // A model going round in circles is told so, rather than left to spend
                    // the whole iteration budget discovering it.
                    if (repeats.Observe(outcome.ToolName, outcome.Result.Output) is { } reminder)
                    {
                        await store.AppendEventAsync(
                            request.ConversationId, ConversationEventType.UserMessage, reminder,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }

                // After the results, not before: read what went wrong, then be asked to
                // rethink. A nudge that arrives ahead of the failures is a non-sequitur.
                if (verdict.Note is not null)
                {
                    await store.AppendEventAsync(
                        request.ConversationId,
                        verdict.AskForRevision ? ConversationEventType.UserMessage : ConversationEventType.ToolResult,
                        verdict.Note,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                progress?.Report(new TurnStep(
                    TurnStage.Plan, PlanSteps: _plan.Steps, PlanDone: _plan.Done));
                progress?.Report(new TurnStep(TurnStage.Recorded));

                // Enough rounds failing in a row, or enough rewrites, and it stops. The
                // round cap would catch it eventually, but only after spending every
                // remaining request discovering the same thing.
                if (verdict.ShouldStop)
                {
                    stopped = verdict.Note;
                    break;
                }
            }
        }
        finally
        {
            runs.Finished(request.ConversationId);
            progress?.Report(new TurnStep(TurnStage.Recorded));
        }

        return new TurnResult(
            last,
            answeredBy?.Runtime.EngineLabel ?? request.Runtime.EngineLabel,
            _plan.Steps,
            _plan.Done,
            stopped);
    }

    /// <summary>
    /// One pass: compose the history, ask, and write down what came back.
    ///
    /// Returns the reply, or null when it was cancelled or nothing arrived — both of which
    /// end the loop, and neither of which is an error worth throwing over.
    /// </summary>
    private async Task<string?> StreamOnceAsync(
        TurnRequest request,
        TurnSettings settings,
        List<ChatImage> images,
        Action<FailoverOutcome> answered,
        IProgress<TurnStep>? progress,
        CancellationToken cancellationToken)
    {
        // Derived from the log rather than read off message rows: the log is what is true,
        // and it is the only place a tool result is distinguishable from something a person
        // typed.
        var turns = (await store.DeriveMessagesAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false)).ToList();

        // Compact before spending a request, not after one is refused for length. On the
        // smallest on-device model this is the difference between a conversation that keeps
        // going and one that stops after a handful of turns.
        var compacted = await compaction
            .CompactIfNeededAsync(turns, options.ContextBudgetTokens, cancellationToken)
            .ConfigureAwait(false);

        if (compacted.WasCompacted)
        {
            turns = compacted.Turns.ToList();

            await store.AppendEventAsync(
                request.ConversationId, ConversationEventType.CompactionReplace,
                $"Summarised the earlier part of this conversation, reclaiming about {compacted.TokensReclaimed} tokens.",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var systemParts = new List<string>();

        // Skills first, so the specialist guidance is read as the identity for the turn and
        // anything specific to this conversation layers on top of it.
        if (settings.SkillIds is { Count: > 0 } active
            && skills.ComposeSystemPrompt(active) is { Length: > 0 } skillPrompt)
        {
            systemParts.Add(skillPrompt);
        }

        if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
        {
            systemParts.Add(settings.SystemPrompt);
        }

        // Asking for a plan only makes sense when there is work to plan: with no tools, or
        // where nothing will be run, it is noise.
        if (settings.IncludeToolCatalog && tools.Tools.Count > 0 && settings.ExecuteTools)
        {
            systemParts.Add(PlanProtocol.SystemPromptAddendum);
        }

        if (settings.IncludeToolCatalog && tools.Tools.Count > 0)
        {
            systemParts.Add(tools.BuildSystemPromptAddendum());
        }

        if (systemParts.Count > 0)
        {
            turns.Insert(0, new ChatTurn("system", string.Join("\n\n", systemParts)));
        }

        // Anything a capability captured since the last turn joins what was handed over.
        // Drained, not read: a screenshot from ten minutes ago silently attached to an
        // unrelated question is worse than no screenshot at all.
        var caught = captured.TakeAll();

        // Drained either way, and only carried to something that can look. A model that
        // cannot see is told a picture was produced rather than handed one it will ignore
        // and then be asked about.
        if (caught.Count > 0
            && request.Runtime is IVisionCapableRuntime { SupportedImageMediaTypes.Count: > 0 })
        {
            images.AddRange(caught);
        }
        else if (caught.Count > 0)
        {
            turns.Add(new ChatTurn(
                "user",
                $"[{string.Join(", ", caught.Select(picture => picture.FileName))} was produced, "
                + $"but {request.Runtime.EngineLabel} cannot look at pictures.]"));
        }

        if (images.Count > 0)
        {
            var lastUser = turns.FindLastIndex(t =>
                string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase));

            if (lastUser >= 0)
            {
                turns[lastUser] = turns[lastUser] with { Images = images.ToArray() };
            }
        }

        var buffer = new StringBuilder();
        var cancelled = false;
        FailoverOutcome? outcome = null;

        // The reply used to be written down only after the last token arrived, so anything
        // that stopped the process mid-generation lost all of it. A checkpoint file carries
        // the partial text while it exists only in memory — throttled, because a write per
        // token is hundreds of writes for one reply and the point is durability.
        var lastCheckpoint = DateTimeOffset.UtcNow;
        var checkpointed = 0;

        try
        {
            // Through the failover chain rather than straight at the chosen runtime. A
            // provider having a bad afternoon used to be a dead turn. Failover decides who
            // may be asked — never off the device, never past a refusal, never after the
            // first token — and the resolver decides the order.
            var alternatives = _resolver.Order(request.Alternatives ?? []);

            await foreach (var chunk in RuntimeFailover.StreamAsync(
                request.Runtime,
                alternatives,
                turns,
                answering =>
                {
                    outcome = answering;
                    answered(answering);

                    // What actually happened, recorded so the next turn is ordered by it:
                    // whoever answered is healthy, and everything it fell back past failed.
                    _resolver.RecordSuccess(answering.Runtime);

                    // Matched on EngineLabel because that is what FellBackFrom carries.
                    // Matching on Id here compiles, runs, and silently never fires.
                    foreach (var label in answering.FellBackFrom)
                    {
                        if ((request.Alternatives ?? []).FirstOrDefault(r =>
                                string.Equals(r.EngineLabel, label, StringComparison.OrdinalIgnoreCase)) is { } failed)
                        {
                            _resolver.RecordFailure(failed);
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false))
            {
                buffer.Append(chunk);
                progress?.Report(new TurnStep(TurnStage.Streaming, Text: buffer.ToString()));

                var now = DateTimeOffset.UtcNow;

                if (buffer.Length - checkpointed >= 240 || (now - lastCheckpoint).TotalMilliseconds >= 1000)
                {
                    await TurnDrafts.WriteAsync(request.ConversationId, buffer.ToString()).ConfigureAwait(false);
                    lastCheckpoint = now;
                    checkpointed = buffer.Length;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception error)
        {
            buffer.AppendLine();
            buffer.Append("[stream error: ").Append(error.Message).Append(']');
            progress?.Report(new TurnStep(TurnStage.Streaming, Text: buffer.ToString()));
        }

        var label = outcome?.Runtime.EngineLabel ?? request.Runtime.EngineLabel;
        var said = buffer.ToString();

        if (cancelled)
        {
            if (said.Length > 0)
            {
                await store.AppendEventAsync(
                    request.ConversationId, ConversationEventType.AssistantMessage,
                    said + "\n[stream cancelled before completion]", label,
                    cancellationToken).ConfigureAwait(false);
            }

            // Whatever happened, the text is in the log now, so the checkpoint has nothing
            // left to protect.
            TurnDrafts.Delete(request.ConversationId);
            progress?.Report(new TurnStep(TurnStage.Recorded));
            return null;
        }

        if (!string.IsNullOrWhiteSpace(said))
        {
            await store.AppendEventAsync(
                request.ConversationId, ConversationEventType.AssistantMessage, said, label, cancellationToken)
                .ConfigureAwait(false);

            TurnDrafts.Delete(request.ConversationId);
            progress?.Report(new TurnStep(TurnStage.Recorded));
            return said;
        }

        TurnDrafts.Delete(request.ConversationId);
        progress?.Report(new TurnStep(TurnStage.Recorded));
        return null;
    }
}

/// <summary>
/// Where a reply is kept while it exists only in memory.
///
/// Shared rather than private to the workspace because the turn writes these and the
/// workspace recovers them, and they are now two different files. Two ideas of where a
/// draft lives is a draft nobody finds.
/// </summary>
public static class TurnDrafts
{
    public static string Directory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Concierge",
        "drafts");

    public static string PathFor(Guid conversationId)
        => System.IO.Path.Combine(Directory, $"{conversationId:N}.partial");

    public static async Task WriteAsync(Guid conversationId, string text)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await File.WriteAllTextAsync(PathFor(conversationId), text).ConfigureAwait(false);
        }
        catch
        {
            // A checkpoint that cannot be written must never interrupt the reply still
            // arriving. Losing durability is bad; losing the generation to a disk error
            // is worse.
        }
    }

    public static void Delete(Guid conversationId)
    {
        try
        {
            var path = PathFor(conversationId);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Left behind, and picked up by recovery on next open. Harmless.
        }
    }
}
