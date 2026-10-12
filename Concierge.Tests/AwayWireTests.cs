using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using Concierge.Away;
using Concierge.Shared.Chat;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// The wire between a device that is not here and the machine that does the making.
///
/// **Driven against a real listener, not a stub of the thing under test.** The watch
/// emulator has no speech recogniser — it says so on the face rather than appearing to
/// listen — so the microphone cannot drive this loop there. That is a fact about a system
/// image, and it is not a reason to claim the wire works without watching it work.
///
/// So the client is plain .NET rather than Android, and this stands a small HTTP server up
/// and makes it answer. What is being checked is the half that actually breaks: the address
/// found from a beacon, the key on the header, the sentence and its situation crossing
/// intact, and a failure reported as a failure.
/// </summary>
public sealed class AwayWireTests : IDisposable
{
    private sealed class CapabilityRuntime(ChatRuntimeCapabilityCatalog catalog) : IChatRuntime, IChatRuntimeCapabilitySource
    {
        public string Id => "circleai";
        public string EngineLabel => "Circle AI on this device";
        public bool IsReady => true;
        public string StatusMessage => "Ready";
        public ChatRuntimeCapabilityCatalog? CapabilityCatalog => catalog;

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return "local";
        }
    }

    private readonly HttpListener _listener = new();
    private readonly List<string> _bodies = [];
    private readonly List<string?> _keys = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _where;
    private string? _capabilitiesResponseOverride;

    private readonly string _outbox = Path.Combine(
        Path.GetTempPath(), $"wire-outbox-{Guid.NewGuid():N}");
    private readonly string _hostConfigRoot = Path.Combine(
        Path.GetTempPath(), $"wire-host-{Guid.NewGuid():N}");
    private string HostConfigPath => Path.Combine(_hostConfigRoot, "desk.txt");

    public AwayWireTests()
    {
        var port = FreePort();
        _where = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{_where}/");
        _listener.Start();

        _ = Task.Run(() => AnswerAsync(_stopping.Token));
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();

        try
        {
            if (Directory.Exists(_outbox))
            {
                Directory.Delete(_outbox, recursive: true);
            }
            if (Directory.Exists(_hostConfigRoot))
            {
                Directory.Delete(_hostConfigRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp folder left behind is nobody's problem.
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AnswerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _keys.Add(context.Request.Headers["X-Concierge-Key"]);

            using (var body = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                _bodies.Add(await body.ReadToEndAsync(cancellationToken));
            }

            var said = context.Request.Url?.AbsolutePath switch
            {
                "/api/away/waiting" =>
                    """[{"id":"11111111-1111-1111-1111-111111111111","tool":"write_file","summary":"Write notes.md","risk":"Caution","askedAt":"2026-09-13T06:14:00Z"}]""",
                "/api/away/capabilities" => _capabilitiesResponseOverride ?? """{"canAnswer":false}""",
                "/api/away/answer" => """{"answered":true}""",
                "/api/away/cancel" => """{"cancelled":true}""",
                _ => """{"understood":true,"what":"Added a title","reply":"Added a title"}""",
            };

            var bytes = Encoding.UTF8.GetBytes(said);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
        }
    }

    [Fact]
    public async Task Capability_wire_drops_activity_when_desktop_is_unavailable_even_if_host_sends_stale_activity()
    {
        var stale = new Concierge.Shared.Chat.ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow.AddHours(-1),
            [new("conversation", "Conversation", Concierge.Shared.Chat.ChatRuntimeCapabilityState.Unavailable,
                "Circle AI stopped while preparing.")],
            new(19, 1300, 8, "Online", null),
            Activity: new Concierge.Shared.Chat.ChatRuntimeActivity(
                "model-download", "embeddings_bf16.bin 1%", 0.01));
        _capabilitiesResponseOverride = System.Text.Json.JsonSerializer.Serialize(
            new AwayDeviceCapabilities(false, stale));
        var client = Pointed();

        var received = await client.GetCapabilitiesAsync();

        Assert.NotNull(received?.CircleAi);
        Assert.Equal(Concierge.Shared.Chat.ChatRuntimeCapabilityState.Unavailable,
            received.CircleAi.Capabilities.Single(capability => capability.Id == "conversation").State);
        Assert.Null(received.CircleAi.Activity);
    }

    [Fact]
    public async Task Capability_wire_preserves_fresh_download_activity_while_conversation_is_available()
    {
        var current = new Concierge.Shared.Chat.ChatRuntimeCapabilityCatalog(
            1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", Concierge.Shared.Chat.ChatRuntimeCapabilityState.Available)],
            new(19, 1300, 8, "Online", null),
            Activity: new Concierge.Shared.Chat.ChatRuntimeActivity(
                "model-download", "Model 10 of 10 · weights 4 GB / 9 GB", 0.45));
        _capabilitiesResponseOverride = System.Text.Json.JsonSerializer.Serialize(
            new AwayDeviceCapabilities(true, current));
        var client = Pointed();

        var received = await client.GetCapabilitiesAsync();

        Assert.Equal("model-download", received?.CircleAi?.Activity?.Kind);
        Assert.Equal(0.45, received?.CircleAi?.Activity?.ProgressRatio);
    }

    /// <summary>
    /// A client already told where the desk is, so discovery is a separate question — and
    /// with an outbox of its own.
    ///
    /// **Its own matters.** Without it these used the real queue folder on whatever machine
    /// the suite runs on: a test that could not reach its listener queued into somebody's
    /// actual outbox, and the next test to point at a live listener delivered it. Which is
    /// how a passing assertion here started depending on what an earlier test had left lying
    /// about on disk.
    /// </summary>
    private AwayClient Pointed()
    {
        var client = new AwayClient(new AwayOutbox(_outbox), hostConfigPath: HostConfigPath) { Key = "watchtest" };
        client.PointAt(_where);
        return client;
    }

    [Fact]
    public void HTTPS_Desktop_link_is_saved_and_loaded_by_a_new_client()
    {
        var client = new AwayClient(new AwayOutbox(_outbox), hostConfigPath: HostConfigPath);

        client.ConfigureHost("https://desk.example.test/");

        Assert.Equal("https://desk.example.test", client.ConfiguredHost);
        var reloaded = new AwayClient(new AwayOutbox(_outbox), hostConfigPath: HostConfigPath);
        Assert.Equal("https://desk.example.test", reloaded.ConfiguredHost);
        Assert.True(reloaded.HasConfiguredHost);
    }

    [Fact]
    public void HTTP_Desktop_link_is_accepted_for_private_LAN_and_rejects_public_cleartext()
    {
        var client = new AwayClient(new AwayOutbox(_outbox), hostConfigPath: HostConfigPath);

        client.ConfigureHost("http://192.168.0.250:5198");

        Assert.Equal("http://192.168.0.250:5198", client.ConfiguredHost);
        Assert.Throws<ArgumentException>(() => client.ConfigureHost("http://desk.example.test"));
        Assert.Equal("http://192.168.0.250:5198", client.ConfiguredHost);
    }

    [Fact]
    public void Reconfiguring_Desktop_preserves_a_saved_key_without_persisting_an_environment_key()
    {
        Directory.CreateDirectory(_hostConfigRoot);
        File.WriteAllLines(HostConfigPath, ["http://192.168.0.250:5198", "pairing-test-key"]);
        var client = new AwayClient(new AwayOutbox(_outbox), hostConfigPath: HostConfigPath);

        client.ConfigureHost("https://desk.example.test");

        Assert.Equal(["https://desk.example.test", "pairing-test-key"], File.ReadAllLines(HostConfigPath));
    }

    /// <summary>
    /// The whole point: a sentence leaves the wrist and an answer comes back in words.
    /// </summary>
    [Fact]
    public async Task A_sentence_crosses_and_an_answer_comes_back()
    {
        var answer = await Pointed().SayAsync(
            "add a heading that says Sports Day",
            new Situation(DateTimeOffset.UtcNow, Motion: "walking"));

        Assert.True(answer.Understood, answer.Reply);
        Assert.Equal("Added a title", answer.Reply);
    }

    [Fact]
    public async Task Project_agent_dispatch_preserves_run_and_project_identity_without_tool_catalogue()
    {
        var runId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var client = Pointed();

        var answer = await client.SayProjectAgentAsync("Review the launch plan.",
            new Situation(DateTimeOffset.UtcNow), runId, projectId, workspaceId, null,
            "Read-only analysis", "{\"Name\":\"Launch\"}");

        Assert.True(answer.Understood, answer.Reply);
        using var body = System.Text.Json.JsonDocument.Parse(Assert.Single(_bodies));
        var request = body.RootElement;
        Assert.Equal(runId, request.GetProperty("requestId").GetGuid());
        Assert.Equal(projectId, request.GetProperty("projectId").GetGuid());
        Assert.Equal(workspaceId, request.GetProperty("projectWorkspaceId").GetGuid());
        Assert.False(request.GetProperty("includeToolCatalog").GetBoolean());
        Assert.Equal((int)ToolPermissionMode.PlanOnly, request.GetProperty("permissionMode").GetInt32());
        Assert.Equal(0, client.Waiting);
    }

    [Fact]
    public async Task Cancellation_uses_the_same_durable_run_id()
    {
        var runId = Guid.NewGuid();
        var cancelled = await Pointed().CancelRunAsync(runId);

        Assert.True(cancelled);
        using var body = System.Text.Json.JsonDocument.Parse(Assert.Single(_bodies));
        Assert.Equal(runId, body.RootElement.GetProperty("runId").GetGuid());
    }

    [Fact]
    public async Task Project_agent_selector_chooses_ready_Desktop_when_it_has_more_usable_memory()
    {
        var desktopCatalog = new ChatRuntimeCapabilityCatalog(1, "circleai", "3.6.0", DateTimeOffset.UtcNow,
            [new("conversation", "Conversation", ChatRuntimeCapabilityState.Available),
             new("reasoning", "Reasoning", ChatRuntimeCapabilityState.Available)],
            new(16.1, 500, 16, "Online", "Live"));
        _capabilitiesResponseOverride = System.Text.Json.JsonSerializer.Serialize(
            new AwayDeviceCapabilities(true, desktopCatalog));
        var mobileCatalog = desktopCatalog with { Resources = desktopCatalog.Resources! with { UsableMemoryGb = 2.1 } };
        var local = new CapabilityRuntime(mobileCatalog);

        var selected = await new AwayProjectAgentRuntimeSelector(Pointed()).SelectAsync(local);

        Assert.IsType<AwayProjectAgentRuntime>(selected);
    }

    /// <summary>
    /// The situation travels with it, and the time is the device's own — something said on a
    /// train and delivered forty minutes later is still something said on a train.
    /// </summary>
    [Fact]
    public async Task And_carries_what_the_device_knew()
    {
        var at = new DateTimeOffset(2026, 9, 13, 6, 14, 0, TimeSpan.Zero);

        await Pointed().SayAsync("make it night", new Situation(at, Motion: "walking", AmbientLux: 3));

        var sent = Assert.Single(_bodies);

        Assert.Contains("walking", sent, StringComparison.Ordinal);
        Assert.Contains("2026-09-13T06:14:00", sent, StringComparison.Ordinal);
        Assert.Contains("\"device\":\"watch\"", sent, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(
            System.Text.Json.JsonDocument.Parse(sent).RootElement.GetProperty("requestId").GetString(), out _));
    }

    [Fact]
    public async Task And_continues_the_phone_conversation_id_on_desktop()
    {
        var id = Guid.NewGuid();

        await Pointed().SayInConversationAsync("continue this work", new Situation(DateTimeOffset.UtcNow), id);

        Assert.Contains($"\"conversationId\":\"{id:D}\"", Assert.Single(_bodies), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_project_run_id_is_the_same_id_sent_to_the_desktop_turn_runner()
    {
        var conversationId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await Pointed().SayInConversationAsync("analyse this Project", new Situation(DateTimeOffset.UtcNow), conversationId,
            cancellationToken: default, permissionMode: ToolPermissionMode.PlanOnly, requestId: runId);

        var sent = System.Text.Json.JsonDocument.Parse(Assert.Single(_bodies)).RootElement;
        Assert.Equal(runId, sent.GetProperty("requestId").GetGuid());
        Assert.Equal((int)ToolPermissionMode.PlanOnly, sent.GetProperty("permissionMode").GetInt32());
    }

    [Theory]
    [InlineData(ToolPermissionMode.PlanOnly, "0")]
    [InlineData(ToolPermissionMode.AskFirst, "1")]
    [InlineData(ToolPermissionMode.ActFreely, "2")]
    public async Task And_carries_the_action_permission_mode_to_desktop(ToolPermissionMode mode, string wireValue)
    {
        var id = Guid.NewGuid();
        await Pointed().SayInConversationAsync("continue this work", new Situation(DateTimeOffset.UtcNow), id,
            Array.Empty<Concierge.Shared.Chat.ConversationEvent>(), permissionMode: mode);

        Assert.Contains($"\"permissionMode\":{wireValue}", Assert.Single(_bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task And_carries_project_context_with_the_same_away_run()
    {
        var conversationId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        const string context = "Current project context: \"Launch plan\".";

        await Pointed().SayInConversationAsync("continue launch work", new Situation(DateTimeOffset.UtcNow), conversationId,
            Array.Empty<Concierge.Shared.Chat.ConversationEvent>(), permissionMode: ToolPermissionMode.AskFirst,
            projectId: projectId, projectWorkspaceId: workspaceId, projectContext: context);

        var sent = System.Text.Json.JsonDocument.Parse(Assert.Single(_bodies)).RootElement;
        Assert.Equal(projectId, sent.GetProperty("projectId").GetGuid());
        Assert.Equal(workspaceId, sent.GetProperty("projectWorkspaceId").GetGuid());
        Assert.Equal(context, sent.GetProperty("projectContext").GetString());
    }

    /// <summary>
    /// **And says nothing about what it does not know.** With no location, the fields are
    /// null rather than zero — a location of 0,0 is worse than no location, because whatever
    /// reads it next believes it.
    /// </summary>
    [Fact]
    public async Task And_never_invents_a_reading()
    {
        await Pointed().SayAsync("make it night", new Situation(DateTimeOffset.UtcNow));

        var sent = Assert.Single(_bodies);

        Assert.Contains("\"latitude\":null", sent, StringComparison.Ordinal);
        Assert.Contains("\"heartRate\":null", sent, StringComparison.Ordinal);
    }

    /// <summary>The key goes on every request, because the endpoint runs turns.</summary>
    [Fact]
    public async Task And_every_request_is_signed()
    {
        var client = Pointed();

        await client.SayAsync("make it night", new Situation(DateTimeOffset.UtcNow));
        await client.WaitingAsync();

        Assert.All(_keys, key => Assert.Equal("watchtest", key));
    }

    /// <summary>What is waiting comes back whole, because a wrist has to show what it decides on.</summary>
    [Fact]
    public async Task What_is_waiting_comes_back()
    {
        var waiting = Assert.Single(await Pointed().WaitingAsync());

        Assert.Equal("write_file", waiting.Tool);
        Assert.Equal("Write notes.md", waiting.Summary);
    }

    /// <summary>And answering one says whether it landed.</summary>
    [Fact]
    public async Task And_answering_one_says_whether_it_landed()
        => Assert.True(await Pointed().AnswerAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), allowed: true));

    /// <summary>
    /// **Nowhere to send it is an answer, not a hang.** A watch asking a question must not
    /// sit on a black face because nothing is running at home.
    /// </summary>
    [Fact]
    public async Task With_nothing_listening_it_says_so()
    {
        var client = new AwayClient(new AwayOutbox(_outbox));
        client.PointAt($"http://127.0.0.1:{FreePort()}");

        var answer = await client.SayAsync("make it night", new Situation(DateTimeOffset.UtcNow));

        Assert.False(answer.Understood);
        Assert.False(string.IsNullOrWhiteSpace(answer.Reply));
    }

    [Fact]
    public async Task A_failed_capability_refresh_forgets_a_stale_desktop_address()
    {
        var client = Pointed();

        var live = await client.GetCapabilitiesAsync();

        Assert.NotNull(live);
        Assert.Equal(_where, client.Where);

        _listener.Close();
        var afterDisconnect = await client.GetCapabilitiesAsync();

        Assert.Null(afterDisconnect);
        Assert.Null(client.Where);
    }

    [Fact]
    public async Task A_silent_host_cannot_hold_the_mobile_capability_status_stale_for_two_minutes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var stalledResponse = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(stop.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        });

        var client = new AwayClient(new AwayOutbox(_outbox));
        client.PointAt($"http://127.0.0.1:{port}");
        var elapsed = Stopwatch.StartNew();

        var capabilities = await client.GetCapabilitiesAsync();

        Assert.Null(capabilities);
        Assert.Null(client.Where);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"the disconnected host probe took {elapsed.Elapsed}");
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stalledResponse);
    }

    /// <summary>
    /// And with no beacon at all it gives up rather than waiting — measured, because a
    /// patience that is actually infinite looks identical to one that works until somebody
    /// is standing in a field with it.
    /// </summary>
    [Fact]
    public async Task And_looking_for_a_desk_that_is_not_there_gives_up()
    {
        var began = DateTimeOffset.UtcNow;
        int isolatedPort;
        using (var portProbe = new UdpClient(0))
            isolatedPort = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;

        var found = await new AwayClient(new AwayOutbox(_outbox), isolatedPort)
            .FindAsync(TimeSpan.FromMilliseconds(400));

        Assert.False(found);
        Assert.True(DateTimeOffset.UtcNow - began < TimeSpan.FromSeconds(5), "it waited far longer than it was told to");
    }
}
