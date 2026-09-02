using System.Text.Json.Nodes;
using Concierge.Shared.Extensibility;
using Concierge.Shared.Rpc;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

/// <summary>
/// What plugin loading must do (parity feature 39): let the running app take on capabilities
/// it was not built with, and give them back.
/// </summary>
/// <remarks>
/// This was reported as blocked earlier, on the claim that .NET could not unload. It can —
/// a collectible <c>AssemblyLoadContext</c> is the mechanism, and it has been in the runtime
/// since .NET Core 3.
/// </remarks>
public sealed class PluginHostTests
{
    [Fact]
    public void An_assembly_can_be_loaded_as_a_plugin()
    {
        using var host = new PluginHost();

        var plugin = host.Load(SharedAssemblyPath());

        Assert.Equal("Concierge.Shared", plugin.Name);
    }

    [Fact]
    public void A_loaded_plugin_is_listed()
    {
        using var host = new PluginHost();
        host.Load(SharedAssemblyPath());

        Assert.Contains("Concierge.Shared", host.Loaded);
    }

    [Fact]
    public void A_plugin_offers_the_tools_it_contains()
    {
        // Concierge.Shared contains tool types with parameterless constructors, so it stands
        // in for a plugin without needing one built specially.
        using var host = new PluginHost();

        var plugin = host.Load(SharedAssemblyPath());

        Assert.All(plugin.Tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Name)));
    }

    [Fact]
    public void A_file_that_is_not_there_is_refused()
    {
        using var host = new PluginHost();

        Assert.Throws<InvalidOperationException>(() => host.Load("no-such-plugin.dll"));
    }

    [Fact]
    public void The_same_plugin_cannot_be_loaded_twice()
    {
        using var host = new PluginHost();
        host.Load(SharedAssemblyPath());

        Assert.Throws<InvalidOperationException>(() => host.Load(SharedAssemblyPath()));
    }

    [Fact]
    public void A_file_that_is_not_an_assembly_is_refused()
    {
        var notAnAssembly = Path.Combine(Path.GetTempPath(), $"concierge-fake-{Guid.NewGuid():N}.dll");
        File.WriteAllText(notAnAssembly, "this is not an assembly");
        using var host = new PluginHost();

        try
        {
            Assert.Throws<InvalidOperationException>(() => host.Load(notAnAssembly));
        }
        finally
        {
            File.Delete(notAnAssembly);
        }
    }

    [Fact]
    public void Unloading_removes_the_plugin()
    {
        using var host = new PluginHost();
        host.Load(SharedAssemblyPath());

        host.Unload("Concierge.Shared");

        Assert.Empty(host.Loaded);
    }

    [Fact]
    public void Unloading_something_that_was_never_loaded_is_harmless()
    {
        using var host = new PluginHost();

        Assert.False(host.Unload("never-loaded"));
    }

    /// <summary>
    /// A built assembly to stand in for a plugin. Using the product's own avoids adding a
    /// second project purely to have something loadable.
    /// </summary>
    private static string SharedAssemblyPath() => typeof(IAgentTool).Assembly.Location;
}

/// <summary>
/// What the typed gateway must do (parity feature 46): let a caller reach the runtime through
/// an interface instead of by spelling method names into dictionaries.
/// </summary>
/// <remarks>
/// Built on <c>DispatchProxy</c> from the base class library, so a typed surface costs no code
/// generator, no build step and no dependency.
/// </remarks>
public sealed class TypedRpcTests
{
    private interface IAgentApi
    {
        [RpcMethod("session/new")]
        Task<SessionCreated> NewSessionAsync(string ownerId);

        [RpcMethod("session/prompt")]
        Task<PromptAnswered> PromptAsync(string sessionId, string prompt);

        Task<SessionCreated> Unnamed();
    }

    private sealed record SessionCreated(string SessionId);

    private sealed record PromptAnswered(string Output);

    [Fact]
    public async Task A_typed_call_reaches_the_named_wire_method()
    {
        await using var peer = new LoopbackPeer(request =>
            JsonRpcMessage.Reply(request.Id!.Value, new JsonObject { ["sessionId"] = request.Method! }));
        await using var rpc = new JsonRpcClient(peer.ClientToServer, peer.ServerToClient, new LineFraming());
        var api = TypedRpcProxy.Create<IAgentApi>(rpc);

        var created = await api.NewSessionAsync("local");

        Assert.Equal("session/new", created.SessionId);
    }

    [Fact]
    public async Task A_typed_result_is_returned_as_its_type()
    {
        await using var peer = new LoopbackPeer(request =>
            JsonRpcMessage.Reply(request.Id!.Value, new JsonObject { ["output"] = "the answer" }));
        await using var rpc = new JsonRpcClient(peer.ClientToServer, peer.ServerToClient, new LineFraming());
        var api = TypedRpcProxy.Create<IAgentApi>(rpc);

        var answered = await api.PromptAsync("a-session", "a question");

        Assert.Equal("the answer", answered.Output);
    }

    [Fact]
    public async Task Arguments_are_sent_under_their_parameter_names()
    {
        // The wire object should read the way the interface does, so a server author and a
        // client author are looking at the same names.
        JsonObject? seen = null;
        await using var peer = new LoopbackPeer(request =>
        {
            seen = request.Params;
            return JsonRpcMessage.Reply(request.Id!.Value, new JsonObject { ["output"] = "x" });
        });
        await using var rpc = new JsonRpcClient(peer.ClientToServer, peer.ServerToClient, new LineFraming());
        var api = TypedRpcProxy.Create<IAgentApi>(rpc);

        await api.PromptAsync("a-session", "a question");

        Assert.Equal("a-session", seen!["sessionId"]!.GetValue<string>());
        Assert.Equal("a question", seen["prompt"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_method_without_an_attribute_uses_its_own_name()
    {
        await using var peer = new LoopbackPeer(request =>
            JsonRpcMessage.Reply(request.Id!.Value, new JsonObject { ["sessionId"] = request.Method! }));
        await using var rpc = new JsonRpcClient(peer.ClientToServer, peer.ServerToClient, new LineFraming());
        var api = TypedRpcProxy.Create<IAgentApi>(rpc);

        Assert.Equal("Unnamed", (await api.Unnamed()).SessionId);
    }

    [Fact]
    public async Task An_error_from_the_server_reaches_the_typed_caller()
    {
        await using var peer = new LoopbackPeer(request =>
            JsonRpcMessage.Error(request.Id!.Value, -32601, "no such method"));
        await using var rpc = new JsonRpcClient(peer.ClientToServer, peer.ServerToClient, new LineFraming());
        var api = TypedRpcProxy.Create<IAgentApi>(rpc);

        await Assert.ThrowsAsync<JsonRpcException>(() => api.NewSessionAsync("local"));
    }
}
