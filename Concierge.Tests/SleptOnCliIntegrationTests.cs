using System.Text.Json.Nodes;
using Concierge.Shared.Butler;
using Concierge.Shared.Tools;

namespace Concierge.Tests;

public sealed class SleptOnCliIntegrationTests
{
    [Fact]
    public async Task A_changed_command_exposes_no_tools()
    {
        var runner = new FixtureRunner(Descriptor(changeSearchToPublish: true));
        var source = new SleptOnCliToolSource("slepton", new TokenAccount(null),
            new Approval(ToolApprovalDecision.Allowed), runner, true);

        var status = await source.ConnectAsync();

        Assert.Contains("contract has changed", status);
        Assert.Empty(source.Tools);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Public_reads_never_receive_the_account_token_and_reject_bad_location()
    {
        var runner = new FixtureRunner(Descriptor());
        var source = new SleptOnCliToolSource("slepton", new TokenAccount("private-token"),
            new Approval(ToolApprovalDecision.Allowed), runner, false);
        await source.ConnectAsync();

        Assert.Equal(6, source.Tools.Count);
        Assert.DoesNotContain(source.Tools, tool => tool.Name == "slepton_whoami");
        var search = source.Tools.Single(tool => tool.Name == "slepton_search");
        Assert.False(search.IsReadOnly); // A network read still asks before it leaves the device.
        var result = await search.InvokeAsync(JsonNode.Parse("""{"query":"--help"}"""));
        Assert.True(result.Success);
        Assert.Equal(["search", "--json", "--", "--help"], runner.Calls.Last().Arguments);
        Assert.Null(runner.Calls.Last().Token);

        var before = runner.Calls.Count;
        var nearby = source.Tools.Single(tool => tool.Name == "slepton_events_nearby");
        var invalid = await nearby.InvokeAsync(JsonNode.Parse("""{"latitude":91,"longitude":0,"radiusKm":20}"""));
        Assert.False(invalid.Success);
        Assert.Equal(before, runner.Calls.Count);
    }

    [Fact]
    public async Task Authenticated_read_passes_token_only_to_child_runner_after_approval()
    {
        var runner = new FixtureRunner(Descriptor());
        var approval = new Approval(ToolApprovalDecision.Denied);
        var source = new SleptOnCliToolSource("slepton", new TokenAccount("private-token"),
            approval, runner, true);
        await source.ConnectAsync();
        Assert.Equal(8, source.Tools.Count);
        var owned = source.Tools.Single(tool => tool.Name == "slepton_apps_owned");

        Assert.False((await owned.InvokeAsync(null)).Success);
        Assert.Single(runner.Calls); // Discovery only; denial never runs the read.

        approval.Decision = ToolApprovalDecision.Allowed;
        Assert.True((await owned.InvokeAsync(null)).Success);
        Assert.Equal(["apps", "owned", "--json"], runner.Calls.Last().Arguments);
        Assert.Equal("private-token", runner.Calls.Last().Token);
        Assert.DoesNotContain("private-token", string.Join(' ', runner.Calls.Last().Arguments));
    }

    private static string Descriptor(bool changeSearchToPublish = false)
    {
        var definitions = new (string Name, string[] Argv, bool Session, string Schema)[]
        {
            ("slepton_search", changeSearchToPublish ? ["publish", "--submit"] : ["search", "--json", "--", "{query}"], false,
                """{"type":"object","properties":{"query":{"type":"string","minLength":2}},"required":["query"]}"""),
            ("slepton_apps_list", ["apps", "list", "--json"], false, Empty),
            ("slepton_apps_show", ["apps", "show", "{id}", "--json"], false, Id),
            ("slepton_events_nearby", ["events", "nearby", "--latitude", "{latitude}", "--longitude", "{longitude}", "--radius-km", "{radiusKm}", "--json"], false,
                """{"type":"object","properties":{"latitude":{"type":"number","minimum":-90,"maximum":90},"longitude":{"type":"number","minimum":-180,"maximum":180},"radiusKm":{"type":"integer","minimum":1,"maximum":10000}},"required":["latitude","longitude","radiusKm"]}"""),
            ("slepton_apps_owned", ["apps", "owned", "--json"], true, Empty),
            ("slepton_whoami", ["whoami", "--json"], true, Empty),
            ("slepton_apps_releases", ["apps", "releases", "{id}", "--json"], false, Id),
            ("slepton_apps_update", ["apps", "update", "--json", "--", "{package}", "{currentVersion}"], false,
                """{"type":"object","properties":{"package":{"type":"string","minLength":3},"currentVersion":{"type":"string","minLength":1}},"required":["package","currentVersion"]}"""),
        };
        var tools = new JsonArray();
        foreach (var definition in definitions)
            tools.Add(new JsonObject
            {
                ["name"] = definition.Name,
                ["description"] = definition.Name,
                ["readOnly"] = true,
                ["requiresSession"] = definition.Session,
                ["argv"] = new JsonArray(definition.Argv.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                ["inputSchema"] = JsonNode.Parse(definition.Schema),
            });
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["ok"] = true,
            ["data"] = new JsonObject { ["adapter"] = new JsonObject { ["version"] = "1.1.0", ["tools"] = tools } },
        }.ToJsonString();
    }

    private const string Empty = """{"type":"object","properties":{},"required":[]}""";
    private const string Id = """{"type":"object","properties":{"id":{"type":"string","format":"uuid"}},"required":["id"]}""";

    private sealed class FixtureRunner(string descriptor) : ISleptOnCliRunner
    {
        public List<(IReadOnlyList<string> Arguments, string? Token)> Calls { get; } = [];
        public Task<SleptOnCliResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            string? accessToken, CancellationToken cancellationToken)
        {
            Calls.Add((arguments, accessToken));
            return Task.FromResult(new SleptOnCliResult(0,
                arguments.SequenceEqual(["integration", "describe", "--json"])
                    ? descriptor : """{"schemaVersion":1,"ok":true,"data":{}}"""));
        }
    }

    private sealed class Approval(ToolApprovalDecision decision) : IToolApprovalService
    {
        public ToolApprovalDecision Decision { get; set; } = decision;
        public ValueTask<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Decision);
    }

    private sealed class TokenAccount(string? token) : IButlerAccountSession
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
        public Task<ButlerAccountStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ButlerSignInResult> SignInAsync(string emailOrMobile, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ButlerSignInResult> CompleteTwoFactorAsync(string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OnlineAccountAuthorization?> GetAuthorizationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
