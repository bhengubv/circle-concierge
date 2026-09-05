using System.Text.Json;
using Concierge.Ai;
using Concierge.Shared.Chat;
using Concierge.Shared.Chat.Isolation;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Model.Host;

/// <summary>
/// Owns the model, and is allowed to die.
///
/// Reads one JSON request per line on stdin and writes one JSON response per
/// line on stdout. Nothing else goes to stdout — every diagnostic goes to
/// stderr — because a stray Console.WriteLine here is a parse error in the
/// parent.
///
/// There is no crash handling in this file, deliberately. An access violation
/// in the native generator cannot be caught in managed code, and trying would
/// only add a layer that fails to. The parent watches the pipe instead: when
/// this process goes, the pipe closes, and that is the signal.
/// </summary>
internal static class Program
{
    private static CircleAiChatRuntime? _runtime;
    private static CancellationTokenSource? _current;

    private static async Task<int> Main()
    {
        // stdin and stdout carry the protocol. Anything the runtime logs must
        // not land in the middle of it.
        var output = Console.Out;
        Console.SetOut(Console.Error);

        try
        {
            _runtime = Build();

            // Load it here rather than waiting for something else to.
            //
            // AddConciergeAi registers a hosted service that calls LoadAsync,
            // and nothing in this process runs hosted services — so without
            // this the runtime sits at "Engine queued for load" forever and the
            // parent is told, correctly, that nothing is ready. Confirmed by
            // running this executable by hand.
            await _runtime.LoadAsync(CancellationToken.None);

            await WriteAsync(output, new ModelHostResponse(
                Ready: _runtime.IsReady,
                EngineLabel: _runtime.EngineLabel,
                Status: _runtime.StatusMessage));
        }
        catch (Exception ex)
        {
            // Failing to load is not the crash this design is about, and the
            // parent can show it. Report and exit cleanly.
            await WriteAsync(output, new ModelHostResponse(
                Ready: false,
                EngineLabel: "unavailable",
                Status: $"The model host could not start: {ex.Message}"));
            return 1;
        }

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            ModelHostRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ModelHostRequest>(line, ModelHostProtocol.Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request is null)
            {
                continue;
            }

            switch (request.Op)
            {
                case ModelHostProtocol.Cancel:
                    _current?.Cancel();
                    break;

                case ModelHostProtocol.Stream:
                    await StreamAsync(output, request);
                    break;
            }
        }

        // stdin closed: the parent has gone, so this should too rather than
        // linger holding a model in memory.
        return 0;
    }

    private static CircleAiChatRuntime Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConciergeAi();

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<CircleAiChatRuntime>();
    }

    private static async Task StreamAsync(TextWriter output, ModelHostRequest request)
    {
        _current?.Dispose();
        _current = new CancellationTokenSource();
        var token = _current.Token;

        var turns = (request.Turns ?? Array.Empty<ModelHostTurn>())
            .Select(t => new ChatTurn(
                t.Role,
                t.Content,
                t.Images?.Select(i => new ChatImage(i.FileName, i.MediaType, Convert.FromBase64String(i.Base64))).ToArray()))
            .ToList();

        try
        {
            await foreach (var chunk in _runtime!.StreamAsync(turns, token))
            {
                await WriteAsync(output, new ModelHostResponse(request.Id, Chunk: chunk));
            }

            await WriteAsync(output, new ModelHostResponse(request.Id, Done: true));
        }
        catch (OperationCanceledException)
        {
            await WriteAsync(output, new ModelHostResponse(request.Id, Done: true));
        }
        catch (Exception ex)
        {
            // A managed failure the parent can show. The unmanaged kind never
            // reaches here — that is what the closed pipe is for.
            await WriteAsync(output, new ModelHostResponse(request.Id, Error: ex.Message, Done: true));
        }
    }

    private static async Task WriteAsync(TextWriter output, ModelHostResponse response)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(response, ModelHostProtocol.Json));
        await output.FlushAsync();
    }
}
