using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Concierge.Shared.Context;
using Concierge.Shared.Media;
using Concierge.Shared.Planning;
using Concierge.Shared.Skills;
using Concierge.Shared.Tools;
using Microsoft.EntityFrameworkCore;

namespace Concierge.Shared.Chat;

/// <summary>Which part of a turn a <see cref="TurnStep"/> is reporting.</summary>
public enum TurnStage
{
    /// <summary>More of the reply has arrived. <see cref="TurnStep.Text"/> is everything so far.</summary>
    Streaming,

    /// <summary>Something was written to the log, so a surface showing it should re-read.</summary>
    Recorded,

    /// <summary>The user's input is durably in the conversation log.</summary>
    UserMessageRecorded,

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
    string? SystemPrompt = null,
    ToolPermissionMode PermissionMode = ToolPermissionMode.AskFirst);

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
    TurnSettings? Settings = null,
    bool InputAlreadyRecorded = false,
    Guid? RunId = null,
    Guid? ProjectId = null,
    Guid? ProjectWorkspaceId = null,
    Guid? StartupJourneyId = null);

/// <summary>What a turn came to.</summary>
public sealed record TurnResult(
    string? Text,
    string? AnsweredBy,
    IReadOnlyList<string> PlanSteps,
    int PlanDone,
    string? Stopped = null);

/// <summary>Durable receipt kept in the conversation event log when a turn closes.</summary>
public sealed record TurnRunStart(
    Guid RunId,
    string Fingerprint,
    Guid? ProjectId = null,
    Guid? ProjectWorkspaceId = null,
    Guid? StartupJourneyId = null);

public sealed record TurnRunReceipt(
    Guid RunId,
    string Status,
    string? Text,
    string? AnsweredBy,
    IReadOnlyList<string> PlanSteps,
    int PlanDone,
    string? Stopped);

/// <summary>A durable snapshot of the plan presented for one turn.</summary>
public sealed record TurnPlanSnapshot(
    Guid RunId,
    IReadOnlyList<string> Steps,
    int Done,
    int Revisions,
    DateTimeOffset UpdatedAt);

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
    RuntimeResolver? resolver = null,
    ConversationRunGate? runGate = null)
{
    private readonly RuntimeResolver _resolver = resolver ?? new RuntimeResolver();
    private readonly ConversationRunGate _runGate = runGate ?? new ConversationRunGate();
    private const string ConciergeIdentityPrompt = """
        You are the assistant inside Concierge, powered by Circle AI. Answer clearly and directly. Ground capabilities in available tools and connected services. Never say a task was executed without confirmation. Butler performs the dispatched operational work when connected. Do not imply Butler is connected without confirmation. Follow the app's action mode and approvals. Circle AI chooses models and resources; never ask the user to choose them. Concierge manages conversations, Workspaces, Projects and progress. Ask only necessary follow-up questions.
        """;

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
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProjectId == Guid.Empty || request.ProjectWorkspaceId == Guid.Empty || request.StartupJourneyId == Guid.Empty
            || request.ProjectWorkspaceId is not null && request.ProjectId is null)
            throw new ArgumentException("Work context requires non-empty IDs and a Workspace's owning Project ID.", nameof(request));

        using var conversationLease = await _runGate.EnterAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false);
        var settings = request.Settings ?? new TurnSettings();
        var images = request.Images is { Count: > 0 } given ? given.ToList() : [];
        var runId = request.RunId is { } requestedId && requestedId != Guid.Empty ? requestedId : Guid.NewGuid();
        var fingerprint = Fingerprint(request, settings, images);

        // The event ID is the stable request identity. SQLite's primary key prevents two
        // copies of the same request from both opening a run, including across processes.
        // A completed retry replays its receipt; an open marker after a crash is reported
        // as interrupted because the native model/tool loop cannot resume mid-call yet.
        var existingRun = (await store.ReadEventsAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false)).FirstOrDefault(entry => entry.Id == runId);
        if (existingRun is not null)
        {
            var recordedStart = TryReadRunStart(existingRun.Data);
            if (existingRun.Type != ConversationEventType.TurnStart || recordedStart?.RunId != runId)
                throw new InvalidOperationException($"Run ID {runId} is already used by another conversation event.");
            if (!string.Equals(recordedStart.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException($"Run ID {runId} was already used for a different request.");

            return await ExistingRunResultAsync(request.ConversationId, runId, existingRun.Seq, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await store.AppendEventAsync(
                request.ConversationId, ConversationEventType.TurnStart,
                JsonSerializer.Serialize(new TurnRunStart(runId, fingerprint, request.ProjectId, request.ProjectWorkspaceId, request.StartupJourneyId)),
                cancellationToken: cancellationToken, eventId: runId).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another process may have committed the same request after the read above.
            // Resolve only a matching run marker; unrelated primary-key/sequence failures
            // still surface to the caller.
            var raced = (await store.ReadEventsAsync(request.ConversationId, cancellationToken)
                .ConfigureAwait(false)).FirstOrDefault(entry => entry.Id == runId);
            if (raced?.Type != ConversationEventType.TurnStart
                || TryReadRunStart(raced.Data) is not { } racedStart
                || racedStart.RunId != runId
                || !string.Equals(racedStart.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw;

            return await ExistingRunResultAsync(request.ConversationId, runId, raced.Seq, cancellationToken)
                .ConfigureAwait(false);
        }

        // Plan progress belongs to this run, not the scoped runner. A turn may
        // outlive its workspace while another conversation starts using the same
        // runner instance; instance state would let those plans overwrite each other.
        var plan = new PlanProgress();

        FailoverOutcome? answeredBy = null;
        string? last = null;
        string? stopped = null;
        var runStatus = "completed";

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runToken = runCancellation.Token;
        runs.Started(request.ConversationId, runCancellation);
        using var approvalRunContext = ToolApprovalRunContext.Enter(request.ConversationId, runId);
        using var projectToolRunContext = ProjectToolRunContext.Enter(
            new ProjectToolRun(request.ProjectId, request.ProjectWorkspaceId, request.ConversationId, runId,
                request.StartupJourneyId, settings.PermissionMode));

        try
        {
            if (!request.InputAlreadyRecorded)
            {
                await store.AppendEventAsync(
                    request.ConversationId, ConversationEventType.UserMessage, request.Text,
                    cancellationToken: runToken).ConfigureAwait(false);
                progress?.Report(new TurnStep(TurnStage.UserMessageRecorded));
            }

            progress?.Report(new TurnStep(TurnStage.Recorded));

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
                    progress, runToken).ConfigureAwait(false);

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
                    var previousSteps = plan.Steps.ToArray();
                    var revised = plan.State(stated);

                    if (!previousSteps.SequenceEqual(plan.Steps, StringComparer.Ordinal))
                        await RecordPlanAsync(request.ConversationId, runId, plan, runToken).ConfigureAwait(false);

                    if (revised)
                    {
                        await store.AppendEventAsync(
                            request.ConversationId, ConversationEventType.ToolResult,
                            $"The plan was revised ({plan.Revisions} so far this turn).",
                            cancellationToken: runToken).ConfigureAwait(false);
                    }

                    progress?.Report(new TurnStep(
                        TurnStage.Plan, PlanSteps: plan.Steps, PlanDone: plan.Done, PlanRevised: revised));
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
                        cancellationToken: runToken).ConfigureAwait(false);
                    break;
                }

                // Each call is recorded before it runs, so a turn abandoned half way still
                // leaves a log that says what was asked for.
                foreach (var call in calls)
                {
                    await store.AppendEventAsync(
                        request.ConversationId, ConversationEventType.ToolCall,
                        call.Name + " " + (call.Arguments?.ToJsonString() ?? "{}"),
                        cancellationToken: runToken).ConfigureAwait(false);
                }

                var planned = calls.Select(call => new PlannedToolCall(call.Name, call.Arguments)).ToList();
                var outcomes = await scheduler.ExecuteAsync(planned, runToken).ConfigureAwait(false);

                // One round of calls is one step done — but only a round where something
                // actually worked. Ticking regardless is the strip asserting progress
                // nobody made, in the place a person looks to decide whether to let it run on.
                var previousDone = plan.Done;
                var verdict = plan.Round(outcomes.Select(o => o.Result.Success).ToList());
                if (plan.Done != previousDone)
                    await RecordPlanAsync(request.ConversationId, runId, plan, runToken).ConfigureAwait(false);

                foreach (var outcome in outcomes)
                {
                    // Oversized output is trimmed once here rather than paid for on every
                    // later request in this conversation.
                    var trimmed = pruner.Prune(outcome.Result.Output);
                    var result = trimmed.WasPruned
                        ? outcome.Result with { Output = trimmed.Text }
                        : outcome.Result;

                    var toolResultEvent = await store.AppendEventAsync(
                        request.ConversationId, ConversationEventType.ToolResult,
                        ToolCallProtocol.FormatResult(outcome.ToolName, result),
                        cancellationToken: runToken).ConfigureAwait(false);

                    foreach (var artifact in result.Artifacts ?? [])
                    {
                        await store.AppendEventAsync(
                            request.ConversationId,
                            ConversationEventType.ArtifactCreated,
                            JsonSerializer.Serialize(new ConversationArtifactRecord(
                                Guid.NewGuid(), artifact.Name, artifact.MediaType, toolResultEvent.Id, runId)),
                            cancellationToken: runToken).ConfigureAwait(false);
                    }

                    // A model going round in circles is told so, rather than left to spend
                    // the whole iteration budget discovering it.
                    if (repeats.Observe(outcome.ToolName, outcome.Result.Output) is { } reminder)
                    {
                        await store.AppendEventAsync(
                            request.ConversationId, ConversationEventType.UserMessage, reminder,
                            cancellationToken: runToken).ConfigureAwait(false);
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
                        cancellationToken: runToken).ConfigureAwait(false);
                }

                progress?.Report(new TurnStep(
                    TurnStage.Plan, PlanSteps: plan.Steps, PlanDone: plan.Done));
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
        catch (OperationCanceledException)
        {
            runStatus = "cancelled";
            throw;
        }
        catch
        {
            runStatus = "failed";
            throw;
        }
        finally
        {
            var receipt = new TurnRunReceipt(
                runId, runStatus, last, answeredBy?.Runtime.EngineLabel ?? request.Runtime.EngineLabel,
                plan.Steps, plan.Done, stopped);
            try
            {
                await store.AppendEventAsync(
                    request.ConversationId, ConversationEventType.TurnEnd,
                    JsonSerializer.Serialize(receipt), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                runs.Finished(request.ConversationId);
                progress?.Report(new TurnStep(TurnStage.Recorded));
            }
        }

        return new TurnResult(
            last,
            answeredBy?.Runtime.EngineLabel ?? request.Runtime.EngineLabel,
            plan.Steps,
            plan.Done,
            stopped);
    }

    private async Task<TurnResult> ExistingRunResultAsync(
        Guid conversationId, Guid runId, int startSequence, CancellationToken cancellationToken)
    {
        var events = await store.ReadEventsAfterAsync(conversationId, startSequence, cancellationToken)
            .ConfigureAwait(false);
        var priorEnd = events.FirstOrDefault(entry => entry.Type == ConversationEventType.TurnEnd
            && TryReadRunReceipt(entry.Data)?.RunId == runId);
        if (priorEnd is not null && TryReadRunReceipt(priorEnd.Data) is { } receipt)
        {
            var stopped = receipt.Stopped;
            if (string.IsNullOrWhiteSpace(stopped))
            {
                stopped = receipt.Status switch
                {
                    "cancelled" => "This run was cancelled. It was not run again.",
                    "failed" => "This run failed. It was not run again.",
                    _ => null,
                };
            }

            return new TurnResult(receipt.Text, receipt.AnsweredBy, receipt.PlanSteps, receipt.PlanDone, stopped);
        }

        var latestPlan = events.Where(entry => entry.Type == ConversationEventType.TodoWrite)
            .Select(entry => TryReadPlanSnapshot(entry.Data))
            .LastOrDefault(snapshot => snapshot?.RunId == runId);
        return new TurnResult(null, null, latestPlan?.Steps ?? [], latestPlan?.Done ?? 0,
            $"Run {runId} was interrupted after it started. Its recorded events are available for review; it was not run again.");
    }

    private static string Fingerprint(TurnRequest request, TurnSettings settings, IReadOnlyList<ChatImage> images)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            request.ConversationId,
            request.Text,
            settings.ExecuteTools,
            settings.IncludeToolCatalog,
            SkillIds = settings.SkillIds ?? [],
            settings.SystemPrompt,
            Images = images.Select(image => new
            {
                image.FileName,
                image.MediaType,
                Digest = Convert.ToHexString(SHA256.HashData(image.Bytes)),
            }),
        });
        // Preserve the pre-context fingerprint for ordinary Chat so durable retries
        // created by earlier versions remain valid. Project-scoped requests include
        // their owner identities, so one request ID cannot be replayed in another scope.
        if (request.ProjectId is not null || request.ProjectWorkspaceId is not null || request.StartupJourneyId is not null)
            canonical += "\n" + JsonSerializer.Serialize(new { request.ProjectId, request.ProjectWorkspaceId, request.StartupJourneyId });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Computes the canonical identity used to reject changed-payload retries.</summary>
    public static string FingerprintRequest(TurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = request.Settings ?? new TurnSettings();
        var images = request.Images is { Count: > 0 } supplied ? supplied : Array.Empty<ChatImage>();
        return Fingerprint(request, settings, images);
    }

    private static TurnRunStart? TryReadRunStart(string data)
    {
        try { return JsonSerializer.Deserialize<TurnRunStart>(data); }
        catch (JsonException) { return null; }
    }

    private static TurnRunReceipt? TryReadRunReceipt(string data)
    {
        try { return JsonSerializer.Deserialize<TurnRunReceipt>(data); }
        catch (JsonException) { return null; }
    }

    private static TurnPlanSnapshot? TryReadPlanSnapshot(string data)
    {
        try { return JsonSerializer.Deserialize<TurnPlanSnapshot>(data); }
        catch (JsonException) { return null; }
    }

    private Task<ConversationEvent> RecordPlanAsync(
        Guid conversationId, Guid runId, PlanProgress plan, CancellationToken cancellationToken)
        => store.AppendEventAsync(conversationId, ConversationEventType.TodoWrite,
            JsonSerializer.Serialize(new TurnPlanSnapshot(runId, plan.Steps, plan.Done, plan.Revisions,
                DateTimeOffset.UtcNow)), cancellationToken: cancellationToken);

    /// <summary>
    /// One pass: compose the history, ask, and write down what came back.
    ///
    /// Returns the reply, or null when nothing arrived. Cancellation is rethrown so the
    /// owning run can persist a cancelled receipt instead of reporting a completed turn.
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
        var history = await store.ReadHistoryAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        var turns = history.Turns.ToList();
        var systemParts = new List<string> { ConciergeIdentityPrompt };

        // Specialist guidance layers onto Concierge's stable product identity, followed by
        // anything specific to this conversation and the tools exposed on this turn.
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
            systemParts.Add(settings.ExecuteTools ? tools.BuildDiscoveryPromptAddendum() : tools.BuildSystemPromptAddendum());
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

        // Measure the complete request, including identity, skills, tools and captured-media notices.
        var promptBudget = Math.Max(1, options.ContextBudgetTokens - Math.Clamp(options.ResponseReserveTokens, 0, Math.Max(0, options.ContextBudgetTokens - 1)));
        var compacted = await compaction.CompactIfNeededAsync(turns, promptBudget, cancellationToken).ConfigureAwait(false);
        if (compacted.WasCompacted)
        {
            turns = compacted.Turns.ToList();
            // The injected first system turn is ephemeral. Keep the exact durable history and cursor;
            // later events beyond that cursor are still included when the snapshot is replayed.
            var durable = turns.Skip(1).Select(t => t with { Images = null }).ToArray();
            await store.AppendEventAsync(request.ConversationId, ConversationEventType.CompactionReplace,
                JsonSerializer.Serialize(new ConversationCompactionSnapshot(1, durable, compacted.TokensReclaimed, history.ThroughSeq)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var buffer = new StringBuilder();
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
            var partial = buffer.ToString();
            if (partial.Length > 0)
            {
                // The caller's token is already cancelled. Persist the partial answer
                // independently, then let RunAsync write the terminal cancelled receipt.
                await store.AppendEventAsync(
                    request.ConversationId, ConversationEventType.AssistantMessage,
                    partial + "\n[stream cancelled before completion]",
                    outcome?.Runtime.EngineLabel ?? request.Runtime.EngineLabel,
                    CancellationToken.None).ConfigureAwait(false);
            }

            TurnDrafts.Delete(request.ConversationId);
            throw;
        }
        catch (ChatRuntimeUnavailableException) { throw; }
        catch (Exception error)
        {
            buffer.AppendLine();
            buffer.Append("[stream error: ").Append(error.Message).Append(']');
            progress?.Report(new TurnStep(TurnStage.Streaming, Text: buffer.ToString()));
        }

        var label = outcome?.Runtime.EngineLabel ?? request.Runtime.EngineLabel;
        var said = buffer.ToString();

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
