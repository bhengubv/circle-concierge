using Concierge.Ai.Isolated;
using Concierge.Shared.Chat;
using Concierge.Shared.Chat.Isolation;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Concierge.Tests;

/// <summary>
/// The model, run somewhere it is allowed to die.
///
/// The generator is native code reached through P/Invoke and it faults with an
/// access violation inside mnn_llm_generate_stream_text. That is not a managed
/// exception — by the time anything could catch it the process is already being
/// torn down — so the only way for Concierge to survive it is to not be the
/// process it happens in.
///
/// These tests use a stand-in host rather than the real model: the real one
/// needs several hundred megabytes of weights and, at time of writing, crashes.
/// What is being tested is the parent's behaviour when a child misbehaves, and
/// a stand-in that misbehaves on demand tests that better than one that only
/// crashes sometimes.
/// </summary>
public sealed class IsolatedRuntimeTests
{
    // ── Finding the host ──────────────────────────────────────────────────

    /// <summary>
    /// A missing host is a deployment mistake, and it should say so. Silently
    /// looking like a model that will not load sends somebody hunting through
    /// the wrong logs.
    /// </summary>
    [Fact]
    public async Task A_missing_host_says_so_rather_than_failing_silently()
    {
        var runtime = new IsolatedChatRuntime(new IsolatedRuntimeOptions
        {
            HostPath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.exe")
        });

        var text = await CollectAsync(runtime);

        Assert.False(runtime.IsReady);
        Assert.Contains("missing", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_explicit_host_path_that_does_not_exist_resolves_to_nothing()
    {
        var options = new IsolatedRuntimeOptions
        {
            HostPath = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.exe")
        };

        Assert.Null(options.ResolveHostPath());
    }

    [Fact]
    public void An_explicit_host_path_that_exists_is_used()
    {
        var path = Path.Combine(Path.GetTempPath(), $"host-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "not really an executable");

        try
        {
            Assert.Equal(path, new IsolatedRuntimeOptions { HostPath = path }.ResolveHostPath());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The registration replaces the in-process runtime rather than joining it.
    /// Two registered runtimes would mean the native library loaded into this
    /// process after all, which is the whole thing being avoided.
    /// </summary>
    [Fact]
    public void Registering_the_isolated_runtime_replaces_rather_than_adds()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatRuntime>(new StubRuntime());

        services.AddConciergeAiIsolated();

        using var provider = services.BuildServiceProvider();
        var runtimes = provider.GetServices<IChatRuntime>().ToList();

        Assert.Single(runtimes);
        Assert.IsType<IsolatedChatRuntime>(runtimes[0]);

        // Synchronously disposable too. A container holding a service that is
        // only IAsyncDisposable refuses Dispose(), and that lands at process
        // exit where it is least welcome.
        Assert.IsAssignableFrom<IDisposable>(runtimes[0]);
    }

    /// <summary>
    /// Same id as the runtime it replaces, so the provider picker, the settings
    /// panel and anything routing by id keep working.
    /// </summary>
    [Fact]
    public void It_keeps_the_identity_of_the_runtime_it_replaces()
        => Assert.Equal("circleai", new IsolatedChatRuntime().Id);

    // ── The wire ──────────────────────────────────────────────────────────

    [Fact]
    public void A_turn_round_trips_through_the_protocol()
    {
        var request = new ModelHostRequest(ModelHostProtocol.Stream, 7, new[]
        {
            new ModelHostTurn("user", "hello"),
        });

        var json = JsonSerializer.Serialize(request, ModelHostProtocol.Json);
        var back = JsonSerializer.Deserialize<ModelHostRequest>(json, ModelHostProtocol.Json)!;

        Assert.Equal(ModelHostProtocol.Stream, back.Op);
        Assert.Equal(7, back.Id);
        Assert.Equal("hello", back.Turns![0].Content);
    }

    /// <summary>
    /// The transport is a line of text, so a picture crosses it base64. Worth
    /// asserting because it is the one place in the codebase that encodes them.
    /// </summary>
    [Fact]
    public void A_picture_survives_the_crossing()
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02 };

        var turn = new ModelHostTurn("user", "what is this?", new[]
        {
            new ModelHostImage("photo.jpg", "image/jpeg", Convert.ToBase64String(bytes)),
        });

        var json = JsonSerializer.Serialize(turn, ModelHostProtocol.Json);
        var back = JsonSerializer.Deserialize<ModelHostTurn>(json, ModelHostProtocol.Json)!;

        Assert.Equal(bytes, Convert.FromBase64String(back.Images![0].Base64));
        Assert.Equal("image/jpeg", back.Images![0].MediaType);
    }

    /// <summary>
    /// A response carrying no chunk and no error is a hello. Kept distinct so
    /// the parent can tell "the model loaded" from "here is a token".
    /// </summary>
    [Fact]
    public void A_hello_is_distinguishable_from_a_chunk()
    {
        var hello = JsonSerializer.Deserialize<ModelHostResponse>(
            JsonSerializer.Serialize(
                new ModelHostResponse(Ready: true, EngineLabel: "Qwen3", Status: "ready"),
                ModelHostProtocol.Json),
            ModelHostProtocol.Json)!;

        Assert.True(hello.Ready);
        Assert.Equal("Qwen3", hello.EngineLabel);
        Assert.Null(hello.Chunk);

        var chunk = JsonSerializer.Deserialize<ModelHostResponse>(
            JsonSerializer.Serialize(new ModelHostResponse(1, Chunk: "tok"), ModelHostProtocol.Json),
            ModelHostProtocol.Json)!;

        Assert.Null(chunk.EngineLabel);
        Assert.Equal("tok", chunk.Chunk);
    }

    /// <summary>
    /// Null fields are dropped on the wire. One line per chunk means the
    /// framing cost is paid per token, and every omitted null is bytes not
    /// serialised, not sent and not parsed.
    /// </summary>
    [Fact]
    public void A_chunk_carries_nothing_it_does_not_need()
    {
        var json = JsonSerializer.Serialize(new ModelHostResponse(1, Chunk: "x"), ModelHostProtocol.Json);

        Assert.DoesNotContain("engineLabel", json);
        Assert.DoesNotContain("error", json);
        Assert.DoesNotContain("null", json);
    }

    /// <summary>
    /// One object per line, so the framing holds. A serializer configured to
    /// indent would put newlines inside a message and desynchronise the reader
    /// on the very first token.
    /// </summary>
    [Fact]
    public void Nothing_on_the_wire_contains_a_newline()
    {
        var json = JsonSerializer.Serialize(
            new ModelHostResponse(1, Chunk: "a chunk with words"), ModelHostProtocol.Json);

        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
    }

    private static async Task<string> CollectAsync(IChatRuntime runtime)
    {
        var text = new System.Text.StringBuilder();

        await foreach (var chunk in runtime.StreamAsync(new[] { new ChatTurn("user", "hello") }))
        {
            text.Append(chunk);
        }

        return text.ToString();
    }

    private sealed class StubRuntime : IChatRuntime
    {
        public string Id => "stub";
        public string EngineLabel => "Stub";
        public bool IsReady => true;
        public string StatusMessage => "ready";

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatTurn> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
