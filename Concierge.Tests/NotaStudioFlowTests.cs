using System.ComponentModel;
using System.Diagnostics;
using Concierge.Shared.Rpc.Mcp;
using Concierge.Shared.Sandboxing;
using Concierge.Shared.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using System.Text.Json.Nodes;
using Concierge.Shared.Media;

namespace Concierge.Tests;

[Collection("Nota studio")]
public sealed class NotaStudioFlowTests
{
    [Fact]
    public async Task Local_nota_session_connects_and_exports_a_playable_draft()
    {
        await using var server = await FakeNota.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "concierge-nota", Guid.NewGuid().ToString("N"));
        await using var studio = new NotaStudio(UnavailableToolApprovalService.Instance, root, server.Endpoint);

        var status = await studio.ConnectAsync();
        Assert.True(status.Connected, status.Message);
        Assert.Contains("export_master", status.ToolNames);
        Assert.Contains("get_overview", status.ToolNames);
        Assert.Contains("120 BPM", await studio.OverviewAsync());
        Assert.Contains("1 track", await studio.OverviewAsync());

        var draft = await studio.ExportAsync("My first song", 16, "wav");
        Assert.True(File.Exists(draft.Path));
        Assert.True(new FileInfo(draft.Path).Length > 44);
        Assert.Equal(1, await ProbeWavSecondsAsync(draft.Path));
        Assert.True(draft.Audible);
        Assert.Equal("wav", draft.Format);
        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(draft.Path), StringComparison.OrdinalIgnoreCase);

        var handoff = await new SleptOnMediaPackageWriter(Path.Combine(root, "handoff"))
            .AudioAsync(draft, rightsConfirmed: true);
        using var archive = System.IO.Compression.ZipFile.OpenRead(handoff.Path);
        Assert.NotNull(archive.GetEntry("audio.wav"));
        Assert.NotNull(archive.GetEntry("manifest.json"));
    }

    [Fact]
    public async Task A_remote_address_is_refused_before_any_connection()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-nota", Guid.NewGuid().ToString("N"));
        await using var studio = new NotaStudio(UnavailableToolApprovalService.Instance, root,
            new Uri("http://192.168.0.50:3900/"));

        var status = await studio.ConnectAsync();
        Assert.False(status.Connected);
        Assert.Contains("this computer", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_nota_reports_a_recoverable_state_and_does_not_offer_tools()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-nota", Guid.NewGuid().ToString("N"));
        await using var studio = new NotaStudio(UnavailableToolApprovalService.Instance, root,
            new Uri("http://127.0.0.1:1/"));

        var status = await studio.ConnectAsync();
        Assert.False(status.Connected);
        Assert.Empty(studio.Tools);
    }

    [Fact]
    public async Task Agent_tools_are_curated_and_require_approval_before_changing_nota()
    {
        await using var server = await FakeNota.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "concierge-nota", Guid.NewGuid().ToString("N"));
        await using var studio = new NotaStudio(UnavailableToolApprovalService.Instance, root, server.Endpoint);
        Assert.True((await studio.ConnectAsync()).Connected);

        var export = Assert.Single(studio.Tools, tool => tool.Name == "nota__export_master");
        Assert.False(export.IsReadOnly);
        var result = await export.InvokeAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = Path.Combine(root, "not-approved.wav"), ["totalBeats"] = 4,
        });
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(root, "not-approved.wav")));
    }

    [Fact]
    public async Task An_approved_agent_note_reaches_nota_after_connection()
    {
        await using var server = await FakeNota.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "concierge-nota", Guid.NewGuid().ToString("N"));
        var approver = new TestApprover();
        await using var studio = new NotaStudio(approver, root, server.Endpoint);
        Assert.Empty(studio.Tools);
        Assert.True((await studio.ConnectAsync()).Connected);

        var note = Assert.Single(studio.Tools, tool => tool.Name == "nota__add_notes");
        var result = await note.InvokeAsync(JsonNode.Parse("""{"trackId":1,"clipIndex":0,"notes":[{"pitch":60,"start":0,"length":1,"velocity":100}]}"""));
        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal(1, FakeNotaTools.NoteCalls);
        Assert.Equal("nota__add_notes", approver.LastRequest?.ToolName);
        Assert.Contains("pitch", approver.LastRequest?.Detail);
    }

    private sealed class TestApprover : IToolApprovalService
    {
        public ToolApprovalRequest? LastRequest { get; private set; }
        public ValueTask<ToolApprovalDecision> RequestAsync(ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(ToolApprovalDecision.Allowed);
        }
    }

    [Fact]
    public async Task An_export_claim_without_a_file_never_appears_as_a_finished_draft()
    {
        await using var server = await FakeNota.StartAsync();
        FakeNotaTools.WriteExport = false;
        var root = Path.Combine(Path.GetTempPath(), "concierge-nota", Guid.NewGuid().ToString("N"));
        await using var studio = new NotaStudio(UnavailableToolApprovalService.Instance, root, server.Endpoint);
        Assert.True((await studio.ConnectAsync()).Connected);
        await Assert.ThrowsAsync<IOException>(() => studio.ExportAsync("missing", 8, "wav"));
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public async Task Silent_audio_is_marked_before_it_can_be_handed_off()
    {
        await using var server = await FakeNota.StartAsync();
        FakeNotaTools.WriteSilent = true;
        var root = Path.Combine(Path.GetTempPath(), "concierge-nota", Guid.NewGuid().ToString("N"));
        await using var studio = new NotaStudio(UnavailableToolApprovalService.Instance, root, server.Endpoint);
        Assert.True((await studio.ConnectAsync()).Connected);
        var draft = await studio.ExportAsync("silent", 4, "wav");
        Assert.False(draft.Audible);
    }

    private static async Task<double> ProbeWavSecondsAsync(string path)
    {
        var start = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var value in new[] { "-v", "error", "-show_entries", "format=duration",
                     "-of", "default=noprint_wrappers=1:nokey=1", path }) start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return double.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed class FakeNota(WebApplication app, Uri endpoint) : IAsyncDisposable
    {
        public Uri Endpoint { get; } = endpoint;

        public static async Task<FakeNota> StartAsync()
        {
            FakeNotaTools.ExportCalls = 0;
            FakeNotaTools.NoteCalls = 0;
            FakeNotaTools.WriteExport = true;
            FakeNotaTools.WriteSilent = false;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<FakeNotaTools>();
            var app = builder.Build();
            app.MapMcp();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            return new FakeNota(app, new Uri(address));
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    [McpServerToolType]
    public sealed class FakeNotaTools
    {
        public static int ExportCalls;
        public static int NoteCalls;
        public static bool WriteExport = true;
        public static bool WriteSilent;

        [McpServerTool(Name = "get_overview"), Description("Describe the open song")]
        public string GetOverview() => """{"bpm":120,"tracks":[{"type":"instrument","name":"Lead","clips":[{"index":0}]}]}""";

        [McpServerTool(Name = "add_notes"), Description("Append notes to a MIDI clip")]
        public string AddNotes(int trackId, int clipIndex, JsonArray notes)
        {
            Interlocked.Increment(ref NoteCalls);
            return $"Added {notes.Count} note to track {trackId}, clip {clipIndex}.";
        }

        [McpServerTool(Name = "export_master"), Description("Export the open song")]
        public string ExportMaster(string path, double totalBeats, string format = "wav")
        {
            Interlocked.Increment(ref ExportCalls);
            if (WriteExport)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = File.Create(path);
                using var writer = new BinaryWriter(file);
                const int samples = 44100;
                writer.Write("RIFF"u8.ToArray()); writer.Write(36 + samples * 2);
                writer.Write("WAVEfmt "u8.ToArray()); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(44100);
                writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8.ToArray()); writer.Write(samples * 2);
                for (var i = 0; i < samples; i++)
                    writer.Write(WriteSilent ? (short)0 : (short)(Math.Sin(2 * Math.PI * 440 * i / 44100) * 12000));
            }
            return path;
        }
    }
}

[CollectionDefinition("Nota studio", DisableParallelization = true)]
public sealed class NotaStudioCollection;
