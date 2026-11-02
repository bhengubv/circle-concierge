using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

/// <summary>
/// The composer: the input, its three controls, and the two things Codex puts
/// directly beneath it — whether the assistant may act without asking, and which
/// model is answering. Both of those were entire screens in this app before.
///
/// Synchronous by design; see WorkspaceHandoffTests for why.
/// </summary>
public sealed class WorkspaceComposerTests : BunitContext
{
    private sealed class MemorySessionState(Concierge.Shared.Session.SessionSnapshot initial) : Concierge.Shared.Session.ISessionState
    {
        public Concierge.Shared.Session.SessionSnapshot Snapshot { get; private set; } = initial;

        public void Replace(Concierge.Shared.Session.SessionSnapshot snapshot) => Snapshot = snapshot;

        public Task<Concierge.Shared.Session.SessionSnapshot> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshot);

        public Task SaveAsync(Concierge.Shared.Session.SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Snapshot = Concierge.Shared.Session.SessionSnapshot.Empty;
            return Task.CompletedTask;
        }
    }

    private MemorySessionState? _sessionState;
    private ReadyRuntime? _readyRuntime;
    private Guid? _fixtureConversationId;

    /// <summary>
    /// A runtime that is simply ready. CanSend gates on IsReady, so without one
    /// the send control can never enable and half of these tests would assert
    /// nothing. It never streams — no test here asks it to answer, which also
    /// keeps them off the native inference path that faults on first call.
    /// </summary>
    private sealed class ReadyRuntime : IChatRuntime
    {
        public string Id => "test";
        public string EngineLabel => "Test-Engine-7B (stub)";
        public bool IsReady => true;
        public string StatusMessage => "ready";
        public TaskCompletionSource<IReadOnlyList<ChatTurn>> SeenMessages { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? AnswerGate { get; init; }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SeenMessages.TrySetResult(messages.ToArray());
            if (AnswerGate is not null) await AnswerGate.WaitAsync(cancellationToken);
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class DownloadingRuntime : IChatRuntime, IModelDownloadRequired, IChatRuntimeStatusEvents
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI";
        public bool IsReady => false;
        public string StatusMessage { get; private set; } = "Downloading a model.";
        public PendingModelDownload? PendingDownload => new("test-model", 1024L * 1024 * 1024, true);
        public ModelDownloadProgress? CurrentDownloadProgress { get; private set; } = new(.2, "20% downloaded");
        public int CancelCount { get; private set; }
        public event EventHandler? StatusChanged;

        public Task<bool> AcceptDownloadAsync(
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task CancelCurrentDownloadAsync()
        {
            CancelCount++;
            CurrentDownloadProgress = null;
            StatusMessage = "Model download stopped.";
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class BackgroundDownloadRuntime : IChatRuntime, IModelDownloadRequired, IChatRuntimeStatusEvents
    {
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _ready;

        public string Id => "circleai";
        public string EngineLabel => "Circle AI";
        public bool IsReady => _ready;
        public string StatusMessage => _ready ? "Ready" : "Setup needed";
        public PendingModelDownload? PendingDownload => _ready ? null : new("test-model", 1024L * 1024 * 1024, true);
        public ModelDownloadProgress? CurrentDownloadProgress { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SynchronizationContext? AcceptSynchronizationContext { get; private set; }
        public event EventHandler? StatusChanged;

        public async Task<bool> AcceptDownloadAsync(
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            AcceptSynchronizationContext = SynchronizationContext.Current;
            CurrentDownloadProgress = new ModelDownloadProgress(.15, "Downloading the first model");
            StatusChanged?.Invoke(this, EventArgs.Empty);
            progress?.Report(CurrentDownloadProgress);
            Started.TrySetResult();
            await _finish.Task.WaitAsync(cancellationToken);
            _ready = true;
            CurrentDownloadProgress = null;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public Task CancelCurrentDownloadAsync()
        {
            _finish.TrySetResult();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return "ok";
        }

        public void Finish() => _finish.TrySetResult();
    }

    private sealed class PreparingRuntime : IChatRuntime, IChatRuntimeCapabilitySource
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI";
        public bool IsReady => false;
        public string StatusMessage => "Loading Qwen model into memory (one-time)…";
        public ChatRuntimeCapabilityCatalog CapabilityCatalog { get; } = new(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Preparing,
                "Loading Qwen model into memory (one-time)…")],
            new(24, 180, 8, "Online", null));

        ChatRuntimeCapabilityCatalog? IChatRuntimeCapabilitySource.CapabilityCatalog => CapabilityCatalog;

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class UnavailableRuntime : IChatRuntime, IChatRuntimeCapabilitySource
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI";
        public bool IsReady => false;
        public string StatusMessage => "No Circle AI model fits this device's available resources. Qwen3-0.6B-MNN is the smallest option.";
        public ChatRuntimeCapabilityCatalog? CapabilityCatalog { get; } = new(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "No Circle AI model fits this device's available resources. Qwen3-0.6B-MNN is the smallest option.")],
            new(0.6, 0.1, 8, "Online", null));

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StartupFailureRuntime : IChatRuntime, IChatRuntimeCapabilitySource
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI";
        public bool IsReady => false;
        public string StatusMessage => "The model stopped unexpectedly.";
        public ChatRuntimeCapabilityCatalog? CapabilityCatalog { get; } = new(
            1,
            "circleai",
            "3.6.0",
            DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Unavailable,
                "Circle AI stopped while preparing. Restart Concierge to try again.")],
            new(28.9, 1300, 8, "Online", null));

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class GeneratedImageRuntime : IImageRuntime
    {
        private readonly bool _produceArtifact;
        public int GenerateCount { get; private set; }
        public GeneratedImageRuntime(bool produceArtifact = true) => _produceArtifact = produceArtifact;
        public string Id => "test-images";
        public string EngineLabel => "Test images";
        public bool IsReady => true;
        public string StatusMessage => "ready";
        public Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
            ImageGenerationRequest request, CancellationToken cancellationToken = default)
        {
            GenerateCount++;
            return Task.FromResult<IReadOnlyList<ImageArtifact>>(_produceArtifact
                ? [new ImageArtifact(Id, request.Prompt, "image/png", null, [1, 2, 3], DateTimeOffset.UtcNow)]
                : []);
        }
    }

    private IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> RenderWith(
        bool runtimeReady,
        string? initialDraft = null,
        bool openConversation = false,
        IChatRuntime? runtimeOverride = null,
        IEnumerable<IImageRuntime>? imageRuntimes = null,
        Concierge.Shared.Session.SessionSnapshot? sessionSnapshot = null,
        Guid? pendingRunId = null,
        string? pendingRunText = null,
        string? pendingRunFingerprint = null,
        bool seedInterruptedRun = false,
        Concierge.Away.AwayClient? desktopClient = null,
        bool windowsDesktop = false,
        string? initialUri = null)
    {
        // Project persistence uses a sibling `projects.db`; give every rendered workspace
        // its own directory so parallel bUnit tests never initialize the same SQLite file.
        var testDataDirectory = Path.Combine(Path.GetTempPath(), $"composer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDataDirectory);
        var dbPath = Path.Combine(testDataDirectory, "chat.db");
        Services.AddLogging();
        Services.AddConciergeCore();

        // The session and skills are in-memory or test-local. Tests must never
        // read or modify the person's real app state.
        Services.AddSingleton(_ => new Concierge.Shared.Skills.UserSkillFolders(
            Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}.json")));

        var sessionState = new MemorySessionState(sessionSnapshot ?? new Concierge.Shared.Session.SessionSnapshot(
            InitialDraftText: initialDraft));
        _sessionState = sessionState;
        Services.AddSingleton<Concierge.Shared.Session.ISessionState>(sessionState);
        Services.AddConciergeChat(dbPath);
        _readyRuntime = new ReadyRuntime();
        Services.AddSingleton<IEnumerable<IChatRuntime>>(_ => runtimeOverride is not null
            ? new[] { runtimeOverride }
            : runtimeReady ? new IChatRuntime[] { _readyRuntime } : Array.Empty<IChatRuntime>());
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeTools(Services);
        Concierge.Shared.Tools.ConciergeToolsServiceCollectionExtensions.AddConciergeRuntime(Services);
        Concierge.Shared.Diagrams.ConciergeDiagramsServiceCollectionExtensions.AddConciergeDiagrams(Services);
        Services.AddSingleton<IEnumerable<IImageRuntime>>(
            _ => imageRuntimes ?? Array.Empty<IImageRuntime>());
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<bool>("conciergeSense.isWindowsDesktop").SetResult(windowsDesktop);
        if (desktopClient is not null)
            Services.AddSingleton(desktopClient);

        using (var db = Services.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>().CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        if (openConversation)
        {
            var store = Services.GetRequiredService<IConversationStore>();
            _fixtureConversationId = store.StartAsync("local", "Project workspace").GetAwaiter().GetResult().Id;
            if (pendingRunId is { } runId && pendingRunText is { } text)
            {
                var request = new TurnRequest(
                    _fixtureConversationId.Value,
                    text,
                    _readyRuntime,
                    Settings: new TurnSettings(
                        ExecuteTools: true,
                        IncludeToolCatalog: true,
                        SkillIds: [],
                        SystemPrompt: string.Empty));
                var imageRuntime = imageRuntimes?.FirstOrDefault(runtime => runtime.IsReady && runtime.Id != "null");
                var fingerprint = pendingRunFingerprint
                    ?? (imageRuntime is not null && text.StartsWith("Generate an image", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
                            {
                                ConversationId = _fixtureConversationId.Value,
                                Prompt = text,
                                RuntimeId = imageRuntime.Id,
                                ProjectId = (Guid?)null,
                                ProjectWorkspaceId = (Guid?)null,
                                StartupJourneyId = (Guid?)null,
                            }))))
                        : TurnRunner.FingerprintRequest(request));
                _sessionState!.Replace(_sessionState.Snapshot with
                {
                    LastConversationId = _fixtureConversationId,
                    PendingTurnSubmissions = new Dictionary<string, Concierge.Shared.Session.PendingTurnSubmission>
                    {
                        [_fixtureConversationId.Value.ToString("N")] = new(_fixtureConversationId.Value, runId, fingerprint),
                    },
                    UnsentText = new Dictionary<string, string>
                    {
                        [_fixtureConversationId.Value.ToString("N")] = text,
                    },
                });

                if (seedInterruptedRun)
                {
                    store.AppendEventAsync(
                        _fixtureConversationId.Value,
                        ConversationEventType.TurnStart,
                        System.Text.Json.JsonSerializer.Serialize(new TurnRunStart(runId, fingerprint)),
                        eventId: runId).GetAwaiter().GetResult();
                }
            }
        }

        // Rendered, then waited for.
        //
        // Render returns after the first pass, and the workspace loads its threads
        // in OnInitializedAsync — so on a loaded machine every assertion made
        // straight afterwards is racing that load. Five tests were fixed one at a
        // time before it was obvious that the fixture was the defect: each looked
        // like an isolated flake, each passed on retry, and the sixth was always
        // going to be somebody else's afternoon.
        //
        // Sidebar chrome exists before data restoration finishes. Wait for the
        // explicit busy state to clear before interacting with the workspace.
        if (initialUri is not null)
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(initialUri);
        var rendered = _fixtureConversationId is { } conversationId
            ? Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>(parameters =>
                parameters.Add(component => component.ConversationId, conversationId))
            : Render<Concierge.Shared.Components.Workspace.Desktop.Workspace>();
        rendered.WaitForState(() => rendered.FindAll(".ws[aria-busy='false']").Count > 0, TimeSpan.FromSeconds(5));
        return rendered;
    }

    [Fact]
    public async Task Windows_workspace_does_not_discover_its_own_Desktop_bridge()
    {
        var client = new Concierge.Away.AwayClient(hostConfigPath:
            Path.Combine(Path.GetTempPath(), $"desktop-self-link-{Guid.NewGuid():N}.txt"));
        client.PointAt("http://127.0.0.1:1");
        var cut = RenderWith(runtimeReady: true, desktopClient: client, windowsDesktop: true);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var method = typeof(Concierge.Shared.Components.Workspace.WorkspaceBase)
            .GetMethod("FindDesktopAsync", flags)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, new object[] { true })!);
        var message = typeof(Concierge.Shared.Components.Workspace.WorkspaceBase)
            .GetField("_desktopSearchMessage", flags)!.GetValue(cut.Instance);
        Assert.Null(message);
        Assert.DoesNotContain("synced to Desktop", cut.Markup);
    }

    private static IElement Send(IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
        => cut.Find("button.icon-btn-send");

    [Fact]
    public async Task Project_workspace_context_reaches_the_assistant_and_durable_run()
    {
        var cut = RenderWith(runtimeReady: true, openConversation: true);
        var conversationStore = Services.GetRequiredService<IConversationStore>();
        var conversationId = _fixtureConversationId!.Value;
        var workflow = new Concierge.Shared.Planning.ProjectWorkflow
        {
            Name = "Launch planning",
            DesiredOutcome = "Prepare a clear launch plan",
        };
        var workspace = new Concierge.Shared.Planning.ProjectWorkspace
        {
            Name = "Customer research",
            DesiredOutcome = "Learn what local customers need",
            WorkflowIds = [workflow.Id],
            ConversationIds = [conversationId],
        };
        var project = Concierge.Shared.Planning.ConciergeProject.Create(
            "Neighborhood launch", "Start a local advertising business", "Business") with
        {
            ConversationIds = [conversationId],
            Workflows = [workflow],
            Workspaces = [workspace],
        };
        project = await Services.GetRequiredService<Concierge.Shared.Planning.IProjectStore>().CreateAsync(project);

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/chat/{conversationId}?project={project.Id}&workspace={workspace.Id}");
        cut.WaitForAssertion(() => Assert.Contains("Customer research", cut.Find(".ws-context-label").TextContent));

        var goalOverview = cut.Find(".ws-goal-overview");
        Assert.Contains("WORKSPACE GOAL", goalOverview.TextContent);
        Assert.Contains("Customer research", goalOverview.TextContent);
        Assert.Contains("Learn what local customers need", goalOverview.TextContent);
        Assert.Contains("Planned", goalOverview.TextContent);
        Assert.Contains("0/1 workflows complete", goalOverview.TextContent);
        Assert.Contains("0 tracked references", goalOverview.TextContent);
        Assert.Equal($"/projects?project={project.Id}&conversation={conversationId}&workspace={workspace.Id}",
            goalOverview.QuerySelector(".ws-goal-open")!.GetAttribute("href"));

        cut.Find("textarea.comp-input").Input("What is next?");
        Send(cut).Click();
        var messages = await _readyRuntime!.SeenMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var systemMessage = Assert.Single(messages.Where(message => message.Role == "system"));
        Assert.Contains("Neighborhood launch", systemMessage.Content);
        Assert.Contains("Customer research", systemMessage.Content);
        Assert.Contains("Launch planning", systemMessage.Content);

        var events = await conversationStore.ReadEventsAsync(conversationId);
        using var runStart = System.Text.Json.JsonDocument.Parse(
            Assert.Single(events, entry => entry.Type == ConversationEventType.TurnStart).Data);
        Assert.Equal(project.Id, runStart.RootElement.GetProperty("ProjectId").GetGuid());
        Assert.Equal(workspace.Id, runStart.RootElement.GetProperty("ProjectWorkspaceId").GetGuid());
    }

    [Fact]
    public async Task Generated_image_is_recorded_as_a_non_model_visible_conversation_artifact()
    {
        var images = new GeneratedImageRuntime();
        var cut = RenderWith(runtimeReady: true, openConversation: true, imageRuntimes: [images]);
        var conversationId = _fixtureConversationId!.Value;
        var project = await Services.GetRequiredService<Concierge.Shared.Planning.IProjectStore>().CreateAsync(
            Concierge.Shared.Planning.ConciergeProject.Create("Campaign", "Create campaign assets", "Business") with
            {
                ConversationIds = [conversationId],
            });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/chat/{conversationId}?project={project.Id}");
        cut.WaitForAssertion(() => Assert.Contains(project.Name, cut.Find(".ws-context-label").TextContent));

        cut.Find("textarea.comp-input").Input("Generate an image of a red circle");
        Send(cut).Click();
        var store = Services.GetRequiredService<IConversationStore>();
        IReadOnlyList<ConversationEvent> events = [];
        cut.WaitForAssertion(() =>
        {
            events = store.ReadEventsAsync(conversationId).GetAwaiter().GetResult();
            Assert.Contains(events, entry => entry.Type == ConversationEventType.TurnEnd);
        });

        var artifactEvent = Assert.Single(events, entry => entry.Type == ConversationEventType.ArtifactCreated);
        var artifact = System.Text.Json.JsonSerializer.Deserialize<ConversationArtifactRecord>(artifactEvent.Data)!;
        Assert.Equal("Generated image", artifact.Name);
        Assert.Equal("image/png", artifact.MediaType);
        Assert.NotNull(artifact.RunId);
        var runStart = Assert.Single(events, entry => entry.Type == ConversationEventType.TurnStart);
        Assert.Equal(artifact.RunId, runStart.Id);
        var runReceipt = System.Text.Json.JsonSerializer.Deserialize<TurnRunReceipt>(
            Assert.Single(events, entry => entry.Type == ConversationEventType.TurnEnd).Data)!;
        Assert.Equal(artifact.RunId, runReceipt.RunId);
        Assert.Equal("completed", runReceipt.Status);
        Assert.Contains(events, entry => entry.Id == artifact.SourceEventId
            && entry.Type == ConversationEventType.AssistantMessage
            && entry.Data.Contains("data:image/png;base64,AQID", StringComparison.Ordinal));

        var projected = await store.DeriveMessagesAsync(conversationId);
        Assert.DoesNotContain(projected, turn => turn.Content == artifactEvent.Data);
    }

    [Fact]
    public async Task Empty_image_generation_closes_the_run_without_submitting_the_prompt_twice()
    {
        var cut = RenderWith(runtimeReady: true, openConversation: true,
            imageRuntimes: [new GeneratedImageRuntime(produceArtifact: false)]);
        var conversationId = _fixtureConversationId!.Value;
        cut.Find("textarea.comp-input").Input("Generate an image of a red circle");
        Send(cut).Click();

        var store = Services.GetRequiredService<IConversationStore>();
        IReadOnlyList<ConversationEvent> events = [];
        cut.WaitForAssertion(() =>
        {
            events = store.ReadEventsAsync(conversationId).GetAwaiter().GetResult();
            Assert.Contains(events, entry => entry.Type == ConversationEventType.TurnEnd);
        });

        var receipt = System.Text.Json.JsonSerializer.Deserialize<TurnRunReceipt>(
            Assert.Single(events, entry => entry.Type == ConversationEventType.TurnEnd).Data)!;
        Assert.Equal("failed", receipt.Status);
        Assert.Single(events, entry => entry.Type == ConversationEventType.UserMessage);
        Assert.DoesNotContain(events, entry => entry.Type == ConversationEventType.ArtifactCreated);
    }

    [Fact]
    public async Task Start_up_handoff_prepares_a_reviewable_draft_and_persists_its_conversation_context()
    {
        var cut = RenderWith(runtimeReady: true);
        var store = Services.GetRequiredService<Concierge.Shared.Planning.IProjectStore>();
        var journey = await store.CreateStartupAsync(Concierge.Shared.Planning.StartupJourney.Create(
            Concierge.Shared.Planning.BusinessJourneyType.ExistingBusiness,
            "Northside Ads",
            "Grow recurring customers and expand to a second area",
            "Two people, ten local customers, mostly one-off campaigns."));

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/chat?startup={journey.Id}");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Start-Up · Northside Ads", cut.Find(".ws-context-label").TextContent);
            Assert.Contains("Help me plan the next steps", cut.Markup);
            Assert.Contains("Two people, ten local customers", cut.Markup);
        });

        cut.Find("textarea.comp-input").Input("Help me plan this business.");
        Send(cut).Click();
        var messages = await _readyRuntime!.SeenMessages.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var systemMessage = Assert.Single(messages.Where(message => message.Role == "system"));
        Assert.Contains("Start-Up context supplied by the person", systemMessage.Content);
        Assert.Contains("Northside Ads", systemMessage.Content);
        Assert.Contains("recurring customers", systemMessage.Content);
        Assert.Contains("Two people, ten local customers", systemMessage.Content);

        var linked = await store.GetStartupAsync(journey.Id);
        var conversationId = Assert.Single(linked!.ConversationIds);
        var events = await Services.GetRequiredService<IConversationStore>().ReadEventsAsync(conversationId);
        Assert.Contains(events, entry => entry.Type == ConversationEventType.UserMessage && entry.Data == "Help me plan this business.");
        using var runStart = System.Text.Json.JsonDocument.Parse(
            Assert.Single(events, entry => entry.Type == ConversationEventType.TurnStart).Data);
        Assert.Equal(journey.Id, runStart.RootElement.GetProperty("StartupJourneyId").GetGuid());

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/chat/{conversationId}");
        cut.WaitForAssertion(() => Assert.Contains("Start-Up · Northside Ads", cut.Find(".ws-context-label").TextContent));
    }

    [Fact]
    public async Task Restored_pending_chat_submission_reuses_its_run_id_until_a_terminal_receipt_exists()
    {
        const string prompt = "Continue the saved business plan.";
        var runId = Guid.NewGuid();
        var cut = RenderWith(
            runtimeReady: true,
            openConversation: true,
            pendingRunId: runId,
            pendingRunText: prompt);
        var conversationId = _fixtureConversationId!.Value;

        Assert.Equal(prompt, cut.Find("textarea.comp-input").GetAttribute("value")
            ?? cut.Find("textarea.comp-input").TextContent);
        Send(cut).Click();

        var store = Services.GetRequiredService<IConversationStore>();
        cut.WaitForAssertion(() =>
        {
            var events = store.ReadEventsAsync(conversationId).GetAwaiter().GetResult();
            Assert.Contains(events, entry => entry.Type == ConversationEventType.TurnEnd);
        });
        var completedEvents = await store.ReadEventsAsync(conversationId);
        Assert.Equal(runId, Assert.Single(completedEvents, entry => entry.Type == ConversationEventType.TurnStart).Id);
        cut.WaitForAssertion(() => Assert.Null(_sessionState!.Snapshot.PendingTurnFor(conversationId)));

        await cut.InvokeAsync(() => cut.Find("textarea.comp-input").Input(prompt));
        Send(cut).Click();
        cut.WaitForAssertion(() =>
        {
            var events = store.ReadEventsAsync(conversationId).GetAwaiter().GetResult();
            Assert.Equal(2, events.Count(entry => entry.Type == ConversationEventType.TurnStart));
        });
        var starts = (await store.ReadEventsAsync(conversationId))
            .Where(entry => entry.Type == ConversationEventType.TurnStart)
            .ToArray();
        Assert.NotEqual(starts[0].Id, starts[1].Id);
    }

    [Fact]
    public async Task Restored_interrupted_submission_is_not_executed_again_and_explains_why()
    {
        const string prompt = "Continue the saved business plan.";
        var runId = Guid.NewGuid();
        var cut = RenderWith(
            runtimeReady: true,
            openConversation: true,
            pendingRunId: runId,
            pendingRunText: prompt,
            seedInterruptedRun: true);
        var conversationId = _fixtureConversationId!.Value;

        Send(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains("was interrupted", cut.Markup, StringComparison.OrdinalIgnoreCase));
        Assert.False(_readyRuntime!.SeenMessages.Task.IsCompleted);
        var events = await Services.GetRequiredService<IConversationStore>().ReadEventsAsync(conversationId);
        Assert.Single(events, entry => entry.Type == ConversationEventType.TurnStart);
        Assert.DoesNotContain(events, entry => entry.Type == ConversationEventType.TurnEnd);
        Assert.Equal(runId, _sessionState!.Snapshot.PendingTurnFor(conversationId)?.RunId);
    }

    [Fact]
    public async Task Restored_interrupted_image_submission_is_not_generated_again()
    {
        const string prompt = "Generate an image of a red circle";
        var runId = Guid.NewGuid();
        var imageRuntime = new GeneratedImageRuntime();
        var cut = RenderWith(
            runtimeReady: true,
            openConversation: true,
            imageRuntimes: [imageRuntime],
            pendingRunId: runId,
            pendingRunText: prompt,
            seedInterruptedRun: true);
        var conversationId = _fixtureConversationId!.Value;

        Send(cut).Click();

        cut.WaitForAssertion(() => Assert.Contains("was interrupted", cut.Markup, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, imageRuntime.GenerateCount);
        var events = await Services.GetRequiredService<IConversationStore>().ReadEventsAsync(conversationId);
        Assert.Single(events, entry => entry.Type == ConversationEventType.TurnStart);
        Assert.DoesNotContain(events, entry => entry.Type == ConversationEventType.TurnEnd);
        Assert.Equal(runId, _sessionState.Snapshot.PendingTurnFor(conversationId)?.RunId);
    }

    // ── When you can send ─────────────────────────────────────────────────

    [Fact]
    public void Send_is_disabled_with_an_empty_composer()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Typing_enables_send()
    {
        var cut = RenderWith(runtimeReady: true);

        cut.Find("textarea.comp-input").Input("Say hello.");

        cut.WaitForAssertion(() => Assert.False(Send(cut).HasAttribute("disabled")));
    }

    /// <summary>
    /// Nothing can be sent to a runtime that has not finished loading, however
    /// much has been typed. This is the guard that stops a prompt vanishing into
    /// a model that is not there yet.
    /// </summary>
    [Fact]
    public void Send_stays_disabled_while_no_runtime_is_ready()
    {
        var cut = RenderWith(runtimeReady: false);

        cut.Find("textarea.comp-input").Input("Say hello.");

        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Composer_shows_indeterminate_progress_when_circle_ai_is_loading_into_memory()
    {
        var cut = RenderWith(runtimeReady: false, runtimeOverride: new PreparingRuntime());
        var progress = cut.Find(".runtime-readiness-progress [role='progressbar']");

        Assert.Null(progress.GetAttribute("aria-valuenow"));
        Assert.Equal("Circle AI preparation progress", progress.GetAttribute("aria-label"));
        Assert.Contains("Circle AI is getting ready on this device", cut.Find(".runtime-readiness-progress").TextContent);
        Assert.Contains("does not report a percentage", cut.Find(".runtime-readiness-progress").TextContent);
        Assert.False(cut.Find(".runtime-readiness").TextContent.Contains("Qwen", StringComparison.OrdinalIgnoreCase));
        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Composer_explains_local_unavailability_without_exposing_the_model_choice()
    {
        var cut = RenderWith(runtimeReady: false, runtimeOverride: new UnavailableRuntime());
        var status = cut.Find(".runtime-readiness").TextContent;

        Assert.Contains("current resources", status);
        Assert.False(status.Contains("Qwen", StringComparison.OrdinalIgnoreCase));
        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Composer_explains_a_native_startup_failure_without_calling_it_a_resource_limit()
    {
        var cut = RenderWith(runtimeReady: false, runtimeOverride: new StartupFailureRuntime());
        var status = cut.Find(".runtime-readiness").TextContent;

        Assert.Contains("stopped while preparing", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restart Concierge", status);
        Assert.DoesNotContain("current resources", status, StringComparison.OrdinalIgnoreCase);
        Assert.False(status.Contains("Qwen", StringComparison.OrdinalIgnoreCase));
        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Query_handoff_releases_the_restoration_gate_while_the_answer_is_still_running()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ReadyRuntime { AnswerGate = gate.Task };
        try
        {
            var cut = RenderWith(runtimeReady: true, runtimeOverride: runtime, initialUri: "/chat?q=Keep%20the%20composer%20usable");
            cut.WaitForAssertion(() =>
            {
                Assert.True(runtime.SeenMessages.Task.IsCompleted);
                Assert.False(gate.Task.IsCompleted);
                Assert.Equal("false", cut.Find(".ws").GetAttribute("aria-busy"));
                Assert.False(cut.Find("textarea.comp-input").HasAttribute("disabled"));
            }, TimeSpan.FromSeconds(5));
        }
        finally { gate.TrySetResult(); }
    }

    [Fact]
    public void A_goal_typed_before_circle_ai_is_ready_is_saved_without_creating_a_conversation()
    {
        var cut = RenderWith(runtimeReady: false);
        const string goal = "Build an MVP for location-based product activation and BI.";

        cut.Find("textarea.comp-input").Input(goal);

        cut.WaitForAssertion(() => Assert.Equal(goal, _sessionState!.Snapshot.InitialDraftText));
        Assert.True(Send(cut).HasAttribute("disabled"));
        Assert.Equal("0", cut.Find(".ws-head-count").TextContent.Trim());
    }

    [Fact]
    public void An_empty_workspace_restores_its_first_run_draft()
    {
        const string goal = "Build an MVP for location-based product activation and BI.";
        var cut = RenderWith(runtimeReady: false, initialDraft: goal);

        Assert.Equal(goal, cut.Find("textarea.comp-input").GetAttribute("value")
            ?? cut.Find("textarea.comp-input").TextContent);
        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Stop_download_dispatches_cancellation_to_the_runtime_and_clears_progress()
    {
        var runtime = new DownloadingRuntime();
        var cut = RenderWith(runtimeReady: false, runtimeOverride: runtime);

        Assert.Contains("20% downloaded", cut.Find(".model-download-status").TextContent);
        cut.Find("button.download-model-cancel").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("button.download-model-cancel")));
        Assert.Equal(1, runtime.CancelCount);
        Assert.Contains("Model download stopped", runtime.StatusMessage);
    }

    [Fact]
    public async Task Workspace_stays_interactive_and_shows_tutorial_during_a_long_model_download()
    {
        var runtime = new BackgroundDownloadRuntime();
        var cut = RenderWith(runtimeReady: false, runtimeOverride: runtime);

        var click = cut.Find("button.download-model-action:not(.download-model-cancel)").ClickAsync();
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(runtime.AcceptSynchronizationContext);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("15%", cut.Find(".model-download-status").TextContent);
            Assert.Contains("Keep exploring while Circle AI prepares", cut.Find(".model-setup-tutorial").TextContent);
        });
        Assert.False(click.IsCompleted, "The download remains active while the workspace has rendered its progress and tutorial.");
        Assert.NotNull(cut.Find("textarea.comp-input"));
        cut.Find("button.model-setup-tutorial-toggle").Click();
        Assert.Contains("Your setup and conversation can continue", cut.Find(".model-setup-tutorial").TextContent);
        Assert.NotNull(cut.Find("textarea.comp-input"));

        runtime.Finish();
        await click.WaitAsync(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".model-setup-tutorial")));
    }

    [Fact]
    public void Setup_tutorial_step_and_pause_state_restore_after_workspace_navigation()
    {
        var initial = new Concierge.Shared.Session.SessionSnapshot(SetupTutorialStep: 1);
        var cut = RenderWith(runtimeReady: false, runtimeOverride: new DownloadingRuntime(), sessionSnapshot: initial);

        Assert.Contains("STEP 2 OF 3", cut.Find(".model-setup-tutorial").TextContent);
        cut.Find("button.model-setup-tutorial-toggle").Click();

        var saved = _sessionState!.Snapshot;
        Assert.Equal(1, saved.SetupTutorialStep);
        Assert.True(saved.SetupTutorialCollapsed);
        Assert.Contains("Tutorial paused at step 2", cut.Find(".model-setup-tutorial").TextContent);

        var restored = Render<Concierge.Shared.Components.Components.ModelSetupTutorial>(parameters => parameters
            .Add(tutorial => tutorial.Show, true)
            .Add(tutorial => tutorial.Busy, true)
            .Add(tutorial => tutorial.Step, saved.SetupTutorialStep)
            .Add(tutorial => tutorial.Collapsed, saved.SetupTutorialCollapsed));
        Assert.Contains("Tutorial paused at step 2", restored.Find(".model-setup-tutorial").TextContent);
        restored.Find("button.model-setup-tutorial-toggle").Click();
        Assert.Contains("STEP 2 OF 3", restored.Find(".model-setup-tutorial").TextContent);
        Assert.Contains("Choose how much control", restored.Find(".model-setup-tutorial").TextContent);
    }

    [Fact]
    public void Sending_a_first_run_draft_moves_it_into_the_conversation_session()
    {
        var cut = RenderWith(runtimeReady: true);
        const string goal = "Build an MVP for location-based product activation and BI.";
        cut.Find("textarea.comp-input").Input(goal);

        cut.Find("button.icon-btn-send").Click();

        cut.WaitForAssertion(() =>
        {
            var session = _sessionState!.Snapshot;
            Assert.Null(session.InitialDraftText);
            Assert.NotNull(session.LastConversationId);
            Assert.Equal(string.Empty, session.UnsentFor(session.LastConversationId!.Value));
        }, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Whitespace_alone_does_not_enable_send()
    {
        var cut = RenderWith(runtimeReady: true);

        cut.Find("textarea.comp-input").Input("    ");

        Assert.True(Send(cut).HasAttribute("disabled"));
    }

    // ── The controls beneath the input ────────────────────────────────────

    /// <summary>
    /// Codex puts "Ask for approval" under the composer. Concierge had it behind
    /// a tab as an entire screen; this is the same decision, in the same place.
    ///
    /// It was a switch, and a switch could not tell the truth here. Off did not
    /// mean "ask" — it meant tools never ran at all — and on still asked before
    /// every write, because the tools ask for themselves. Three real states
    /// need three choices.
    /// </summary>
    [Fact]
    public void What_it_may_do_is_chosen_under_the_composer()
    {
        var cut = RenderWith(runtimeReady: true);

        var options = cut.FindAll(".comp-controls .seg .seg-opt")
            .Select(e => e.TextContent.Trim())
            .ToArray();

        Assert.Equal(new[] { "Plan only", "Ask first", "Act freely" }, options);
    }

    /// <summary>
    /// Asking first is the default, because it is the product's actual promise.
    /// </summary>
    [Fact]
    public void It_asks_first_unless_told_otherwise()
    {
        var cut = RenderWith(runtimeReady: true);

        var chosen = cut.FindAll(".comp-controls .seg .seg-opt")
            .Single(e => e.GetAttribute("aria-pressed") == "true");

        Assert.Equal("Ask first", chosen.TextContent.Trim());
    }

    [Fact]
    public void Sidebar_and_send_controls_emit_explicit_ARIA_boolean_values()
    {
        var cut = RenderWith(runtimeReady: false);

        Assert.All(cut.FindAll(".ws-head[aria-expanded]"), button =>
            Assert.Contains(button.GetAttribute("aria-expanded"), new[] { "true", "false" }));
        Assert.Equal("true", cut.Find("button.icon-btn-send").GetAttribute("aria-disabled"));
    }

    [Fact]
    public void Choosing_a_mode_marks_it()
    {
        var cut = RenderWith(runtimeReady: true);

        cut.FindAll(".comp-controls .seg .seg-opt").Single(e => e.TextContent.Trim() == "Plan only").Click();

        var chosen = cut.FindAll(".comp-controls .seg .seg-opt")
            .Single(e => e.GetAttribute("aria-pressed") == "true");

        Assert.Equal("Plan only", chosen.TextContent.Trim());
    }

    /// <summary>
    /// Each says what it means. A person choosing here is deciding what
    /// software may do to their machine, and three verbs alone do not say.
    /// </summary>
    [Fact]
    public void Each_mode_explains_itself()
    {
        var cut = RenderWith(runtimeReady: true);

        foreach (var option in cut.FindAll(".comp-controls .seg .seg-opt"))
        {
            Assert.False(string.IsNullOrWhiteSpace(option.GetAttribute("title")));
        }
    }

    /// <summary>
    /// Which model is answering used to be the third group of a Settings page.
    /// </summary>
    [Fact]
    public void The_model_answering_is_named_under_the_composer()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.Contains("Circle AI", cut.Find(".comp-controls").TextContent);
    }

    // ── The three icon controls ───────────────────────────────────────────

    /// <summary>
    /// InputFile renders a bare file input, which on Windows draws a grey
    /// "Choose File / No file chosen" button. It is stretched invisibly over the
    /// paperclip instead — same element, same behaviour, no platform furniture.
    /// </summary>
    [Fact]
    public void The_attach_control_hides_the_platform_file_input()
    {
        var cut = RenderWith(runtimeReady: true);

        var input = cut.Find("input[type=file].file-over");
        Assert.Equal("LABEL", input.ParentElement!.TagName);
        Assert.Contains("icon-btn", input.ParentElement.ClassName);
    }

    /// <summary>
    /// Every icon control keeps its name for a screen reader even though only the
    /// glyph is shown.
    /// </summary>
    [Fact]
    public void Every_icon_control_still_has_a_name()
    {
        var cut = RenderWith(runtimeReady: true);

        var names = cut.FindAll(".comp-box .sr").Select(e => e.TextContent.Trim()).ToArray();

        // Renamed when the picker started accepting pictures too.
        Assert.Contains("Attach a file or a picture", names);
        Assert.Contains("Speak", names);
        Assert.Contains("Send", names);
    }

    [Fact]
    public void The_composer_invites_you_to_type()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.Equal("Ask me anything", cut.Find("textarea.comp-input").GetAttribute("placeholder"));
    }

    // ── The empty screen ──────────────────────────────────────────────────

    /// <summary>
    /// The composer is present with no conversation open. It used to appear only
    /// once a conversation existed, so opening Chat gave you a heading and a
    /// button whose only job was to reveal the thing you came for.
    /// </summary>
    [Fact]
    public void The_composer_is_there_before_any_conversation_exists()
    {
        var cut = RenderWith(runtimeReady: true);

        // Waited for, not read once. The empty state is only correct after the
        // thread list has loaded in OnInitializedAsync, so under a loaded machine
        // the first render can be neither empty nor populated — the same flake the
        // sidebar had, seen about once in a hundred runs and passing on retry,
        // which is the worst way for a test to be wrong.
        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find(".comp-box textarea.comp-input"));
            Assert.NotNull(cut.Find(".empty"));
            Assert.Empty(cut.FindAll(".ws-thread"));
        });
    }

    // ── The composer while a canvas is open ───────────────────────────────

    /// <summary>
    /// Opens the design canvas the way a person does — the sidebar entry.
    /// </summary>
    private static void OpenDesign(
        IRenderedComponent<Concierge.Shared.Components.Workspace.Desktop.Workspace> cut)
    {
        var tools = cut.FindAll("button.ws-head")
            .First(b => b.QuerySelector(".ws-head-name")!.TextContent.Trim() == "Make something");
        tools.Click();

        cut.WaitForState(() => cut.FindAll("button.ws-room-btn").Count > 0);
        cut.FindAll("button.ws-room-btn").First(b => b.TextContent.Contains("Creator")).Click();
        cut.WaitForState(() => cut.FindAll(".dz").Count > 0);
    }

    /// <summary>
    /// The worst of them. Closing the canvas threw the whole design away — one
    /// click, an afternoon gone, no warning and nothing to go back to. The surface
    /// is built on the promise that every state you have seen is one tap away, and
    /// its own close button was breaking it.
    /// </summary>
    [Fact]
    public void Closing_the_canvas_puts_it_away_rather_than_throwing_it_out()
    {
        var cut = RenderWith(runtimeReady: true);
        OpenDesign(cut);

        cut.Find("textarea.comp-input").Input("add a title that says Sports Day");
        cut.Find("button.icon-btn-send").Click();
        cut.WaitForState(() => cut.FindAll(".dz-moment").Count > 1);

        var moments = cut.FindAll(".dz-moment").Count;

        // Away, and back again.
        cut.FindAll("button.ws-room-btn").First(b => b.TextContent.Contains("Creator")).Click();
        cut.WaitForState(() => cut.FindAll(".dz").Count == 0);
        cut.FindAll("button.ws-room-btn").First(b => b.TextContent.Contains("Creator")).Click();
        cut.WaitForState(() => cut.FindAll(".dz").Count > 0);

        Assert.Equal(moments, cut.FindAll(".dz-moment").Count);
    }

    /// <summary>
    /// "Ask me anything" while a canvas is open asks for something that is not
    /// going to happen: nothing is being asked and no model is answering.
    /// </summary>
    [Fact]
    public void The_composer_stops_asking_you_to_ask_it_anything()
    {
        var cut = RenderWith(runtimeReady: true);
        OpenDesign(cut);

        Assert.Contains("on the page", cut.Find("textarea.comp-input").GetAttribute("placeholder"));
    }

    /// <summary>
    /// The permission modes and the engine name are both about a model answering,
    /// and with a canvas open none is. Worse than useless: the row said "Ask first"
    /// while Design deliberately acts without asking, advertising the opposite of
    /// what was happening.
    /// </summary>
    [Fact]
    public void The_permission_modes_are_not_offered_where_they_do_not_apply()
    {
        var cut = RenderWith(runtimeReady: true);

        Assert.NotEmpty(cut.FindAll(".seg-opt"));

        OpenDesign(cut);

        Assert.Empty(cut.FindAll(".seg-opt"));
    }

    /// <summary>
    /// The canvas needs no engine, so gating it on one would leave Design dead
    /// while a model loads — or permanently, on a machine where the model is
    /// broken, which is this one.
    /// </summary>
    [Fact]
    public void The_canvas_works_with_no_engine_at_all()
    {
        var cut = RenderWith(runtimeReady: false);
        OpenDesign(cut);

        cut.Find("textarea.comp-input").Input("add a title that says It works");

        Assert.False(cut.Find("button.icon-btn-send").HasAttribute("disabled"));
    }
}
