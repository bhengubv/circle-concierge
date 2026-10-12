using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Butler;
using Concierge.Shared.Planning;

namespace Concierge.Shared.Tools;

/// <summary>Runs Butler's real, read-only recruitment search and waits for its durable terminal receipt.</summary>
internal sealed class ButlerFindJobsTool(IButlerWorkRunApi butler, IProjectStore projects) : IAgentTool
{
    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["keyword"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 160 },
            ["location"] = new JsonObject { ["type"] = "string", ["maxLength"] = 120 },
        },
        ["required"] = new JsonArray("keyword"),
        ["additionalProperties"] = false,
    };

    public string Name => "butler_find_jobs";
    public string Description => "Ask Butler to search public job listings by keyword and optional location for the active Project. This is read-only. Wait for Butler's run result; never invent listings or claim success without a completed run receipt.";
    public JsonNode? ArgumentsSchema => Schema.DeepClone();
    public bool IsReadOnly => true;

    public async Task<AgentToolResult> InvokeAsync(JsonNode? arguments, CancellationToken cancellationToken = default)
    {
        var context = ProjectToolRunContext.Current;
        if (context?.ProjectId is not { } projectId || context.RunId == Guid.Empty)
            return Failure("Butler job search is available only inside a Project conversation.");
        if (context.PermissionMode == ToolPermissionMode.PlanOnly)
            return Failure("This turn is Plan only. Butler was not asked to execute the search.");
        if (!ReadString(arguments, "keyword", out var keyword) || string.IsNullOrWhiteSpace(keyword) || keyword.Length > 160)
            return Failure("Provide a job-search keyword of 1 to 160 characters.");
        if (!ReadString(arguments, "location", out var location, optional: true) || location.Length > 120)
            return Failure("The location must be 120 characters or fewer.");

        Guid? acceptedRunId = null;
        try
        {
            var catalogue = await butler.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (!catalogue.Capabilities.Any(capability => capability.Id == "find_jobs" && capability.Risk == "read_only"))
                return Failure("The connected Butler does not currently offer read-only job search.");

            var request = new ButlerWorkRunRequest(
                context.RunId,
                $"{context.RunId:N}:butler:find_jobs",
                WorkId: null,
                ProjectId: projectId,
                WorkspaceId: context.WorkspaceId,
                ConversationId: context.ConversationId,
                OperationId: "find_jobs",
                ActionMode: context.PermissionMode.ToString(),
                ApprovalReferences: [],
                Arguments: JsonSerializer.SerializeToElement(new { keyword = keyword.Trim(), location = location.Trim() }));
            var summary = $"Search public job listings for {keyword.Trim()}" + (string.IsNullOrWhiteSpace(location) ? string.Empty : $" in {location.Trim()}");
            await SaveProjectRunAsync(projectId, run => run with
            {
                ConciergeRunId = context.RunId,
                IdempotencyKey = request.IdempotencyKey,
                RequestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                ButlerRunId = Guid.Empty,
                WorkspaceId = context.WorkspaceId,
                ConversationId = context.ConversationId,
                OperationId = request.OperationId,
                ActionMode = request.ActionMode,
                Summary = summary,
                Status = "dispatch_pending",
                UpdatedAt = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
            var accepted = await butler.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
            acceptedRunId = accepted.ButlerRunId;
            await SaveProjectRunAsync(projectId, run => run with
            {
                ConciergeRunId = context.RunId,
                IdempotencyKey = request.IdempotencyKey,
                RequestJson = null,
                ButlerRunId = accepted.ButlerRunId,
                WorkspaceId = context.WorkspaceId,
                ConversationId = context.ConversationId,
                OperationId = request.OperationId,
                ActionMode = request.ActionMode,
                Summary = summary,
                Status = accepted.Status,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            long cursor = -1;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var envelope = await butler.GetRunAsync(accepted.ButlerRunId, cancellationToken).ConfigureAwait(false);
                var updates = await butler.ReadEventsAsync(accepted.ButlerRunId, cursor, cancellationToken).ConfigureAwait(false);
                if (updates.Count > 0)
                    cursor = updates.Max(item => item.Sequence);
                await SaveProjectRunAsync(projectId, run => run with
                {
                    Status = envelope.Run.Status,
                    CancellationRequested = envelope.Run.CancellationRequested,
                    ResultJson = envelope.Run.Result?.GetRawText(),
                    Problem = envelope.Run.Problem,
                    LastEventSequence = Math.Max(run.LastEventSequence, cursor),
                    Events = MergeEvents(run.Events, updates),
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, cancellationToken).ConfigureAwait(false);
                switch (envelope.Run.Status.ToLowerInvariant())
                {
                    case "completed":
                        return new AgentToolResult(true, FormatCompleted(envelope));
                    case "failed":
                        return Failure($"Butler's job search failed: {envelope.Run.Problem ?? "No failure detail was returned."} Butler run {envelope.Run.Id:D}.");
                    case "cancelled":
                        return Failure($"Butler cancelled the job search. Butler run {envelope.Run.Id:D}.");
                    case "queued":
                    case "running":
                        await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        return Failure($"Butler returned an unknown run state '{envelope.Run.Status}'. Butler run {envelope.Run.Id:D}.");
                }
            }

            return Failure($"Butler has not returned a terminal result yet. Its run {accepted.ButlerRunId:D} is retained for status recovery.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (acceptedRunId is { } runId)
                {
                    await butler.CancelAsync(runId, CancellationToken.None).ConfigureAwait(false);
                }
                await SaveProjectRunAsync(projectId, run => run with
                {
                    Status = acceptedRunId is null ? "dispatch_pending" : "cancellation_requested",
                    CancellationRequested = true,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { }
            throw;
        }
        catch (ButlerAccountRequiredException exception)
        {
            return Failure(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            return Failure($"Concierge could not confirm a Butler result: {exception.Message}");
        }
        catch (JsonException)
        {
            return Failure("Butler returned a result Concierge could not read.");
        }

        string FormatCompleted(ButlerWorkRunEnvelope envelope)
        {
            var events = envelope.Events.OrderBy(item => item.Sequence)
                .Select(item => item.Type == "progress" && item.Data.TryGetProperty("stage", out var stage)
                    ? stage.GetString()
                    : item.Type)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var result = envelope.Run.Result?.GetRawText() ?? "null";
            if (result.Length > 24_000)
                result = result[..24_000] + "… [result shortened]";
            using var resultDocument = JsonDocument.Parse(result);
            return JsonSerializer.Serialize(new
            {
                source = "Butler",
                status = envelope.Run.Status,
                butlerRunId = envelope.Run.Id,
                conciergeRunId = envelope.Run.ConciergeRunId,
                progress = events,
                result = resultDocument.RootElement,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
    }

    private async Task SaveProjectRunAsync(Guid projectId, Func<ProjectButlerRun, ProjectButlerRun> update,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var project = await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The Project owning this Butler run no longer exists.");
            var existing = project.ButlerRuns.LastOrDefault(run => run.ConciergeRunId == ProjectToolRunContext.Current!.RunId);
            var seed = existing ?? new ProjectButlerRun
            {
                ConciergeRunId = ProjectToolRunContext.Current!.RunId,
                ButlerRunId = Guid.Empty,
                OperationId = "find_jobs",
                Summary = "Searching public job listings",
            };
            var updated = update(seed);
            var runs = project.ButlerRuns.Where(run => run.Id != updated.Id)
                .Append(updated).ToArray();
            try
            {
                await projects.SaveAsync(project with { ButlerRuns = runs }, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ProjectRevisionConflictException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new ProjectRevisionConflictException(projectId);
    }

    private static IReadOnlyList<ProjectButlerEvent> MergeEvents(IReadOnlyList<ProjectButlerEvent> existing,
        IReadOnlyList<ButlerWorkRunEvent> updates)
    {
        var bySequence = existing.ToDictionary(item => item.Sequence);
        foreach (var item in updates)
            bySequence[item.Sequence] = new ProjectButlerEvent(item.Sequence, item.Type,
                item.Data.GetRawText(), item.CreatedAt);
        return bySequence.Values.OrderBy(item => item.Sequence).ToArray();
    }

    private static bool ReadString(JsonNode? arguments, string name, out string value, bool optional = false)
    {
        if (arguments is JsonObject properties)
        {
            if (!properties.TryGetPropertyValue(name, out var node))
            {
                value = string.Empty;
                return optional;
            }
            if (node is JsonValue json && json.TryGetValue<string>(out var text))
            {
                value = text;
                return true;
            }
        }
        value = string.Empty;
        return optional && arguments is not JsonObject;
    }

    private static AgentToolResult Failure(string message) => new(false, string.Empty, message);
}
