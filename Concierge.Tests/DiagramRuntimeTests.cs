using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Concierge.Diagrams.Design;
using Concierge.Shared.Diagrams;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class DiagramRuntimeTests
{
    // ------------------------------------------------------------------ Mermaid

    [Fact]
    public void Mermaid_runtime_classifies_known_diagram_headers()
    {
        var runtime = new MermaidDiagramRuntime();

        var flowchart = runtime.FromSource("flowchart TD\n  A --> B");
        var sequence = runtime.FromSource("sequenceDiagram\n  Alice->>Bob: Hi");
        var er = runtime.FromSource("erDiagram\n  CUSTOMER ||--o{ ORDER : places");

        Assert.NotNull(flowchart);
        Assert.Equal("flowchart", flowchart!.Kind);
        Assert.Equal("mermaid", flowchart.RuntimeId);

        Assert.NotNull(sequence);
        Assert.Equal("sequence", sequence!.Kind);

        Assert.NotNull(er);
        Assert.Equal("er", er!.Kind);
    }

    [Fact]
    public void Mermaid_runtime_rejects_non_mermaid_source()
    {
        var runtime = new MermaidDiagramRuntime();
        Assert.Null(runtime.FromSource(""));
        Assert.Null(runtime.FromSource("just regular text"));
        Assert.Null(runtime.FromSource("```mermaid\nflowchart TD\nA-->B\n```"));
    }

    [Fact]
    public void Mermaid_runtime_extracts_fenced_blocks_from_streamed_text()
    {
        var streamed = """
        Here is the flow you asked for:

        ```mermaid
        flowchart LR
            Start --> Stop
        ```

        And the sequence:

        ```mermaid
        sequenceDiagram
          A->>B: ping
          B->>A: pong
        ```

        That's it.
        """;

        var blocks = MermaidDiagramRuntime.ExtractFencedBlocks(streamed);

        Assert.Equal(2, blocks.Count);
        Assert.Contains("flowchart LR", blocks[0].Source, StringComparison.Ordinal);
        Assert.Contains("sequenceDiagram", blocks[1].Source, StringComparison.Ordinal);
        Assert.True(blocks[0].Start < blocks[1].Start, "Blocks should be returned in document order.");
    }

    // ------------------------------------------------------------------ PenPot

    [Fact]
    public void Penpot_runtime_flattens_pages_and_shapes_into_elements()
    {
        var json = """
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "name": "Landing page experiments",
          "data": {
            "pages": ["22222222-2222-2222-2222-222222222222"],
            "pages-index": {
              "22222222-2222-2222-2222-222222222222": {
                "id": "22222222-2222-2222-2222-222222222222",
                "name": "Hero variants",
                "objects": {
                  "00000000-0000-0000-0000-000000000000": { "id": "root", "name": "root", "type": "frame" },
                  "shape-a": { "id": "shape-a", "name": "Hero frame", "type": "frame", "shapes": ["shape-b"] },
                  "shape-b": { "id": "shape-b", "name": "Headline", "type": "text", "parent-id": "shape-a" },
                  "shape-c": { "id": "shape-c", "name": "Primary CTA", "type": "rect", "parent-id": "shape-a" }
                }
              }
            }
          }
        }
        """;

        var runtime = new PenPotDiagramRuntime();
        var artifact = runtime.FromSource(json);

        Assert.NotNull(artifact);
        Assert.Equal("penpot", artifact!.RuntimeId);
        Assert.Equal("Landing page experiments", artifact.Title);
        Assert.Equal("ui-mockup", artifact.Kind);

        // 1 page + 3 non-root shapes = 4 elements; root sentinel skipped.
        Assert.Equal(4, artifact.Elements.Count);
        Assert.Contains(artifact.Elements, e => e.ElementType == "page" && e.Name == "Hero variants");
        Assert.Contains(artifact.Elements, e => e.ElementType == "frame" && e.Name == "Hero frame");
        Assert.Contains(artifact.Elements, e => e.ElementType == "text" && e.Name == "Headline");
        Assert.Contains(artifact.Elements, e => e.ParentId == "shape-a");
        Assert.Contains("1 page", artifact.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Penpot_runtime_imports_from_a_stream()
    {
        var json = """
        { "id": "33333333-3333-3333-3333-333333333333", "name": "Stream import", "data": { "pages": [], "pages-index": {} } }
        """;
        var runtime = new PenPotDiagramRuntime();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var artifact = await runtime.ImportAsync("design.penpot.json", stream);

        Assert.Equal("Stream import", artifact.Title);
        Assert.Empty(artifact.Elements);
    }

    // ------------------------------------------------------------------ Figma

    [Fact]
    public void Figma_runtime_walks_node_tree_preserving_parent_links()
    {
        var json = """
        {
          "name": "Marketing site",
          "lastModified": "2025-01-04T08:00:00Z",
          "version": "12345",
          "document": {
            "id": "0:0",
            "name": "Document",
            "type": "DOCUMENT",
            "children": [
              {
                "id": "0:1",
                "name": "Home",
                "type": "CANVAS",
                "children": [
                  {
                    "id": "1:2",
                    "name": "Hero",
                    "type": "FRAME",
                    "children": [
                      { "id": "1:3", "name": "Headline", "type": "TEXT" }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;
        var runtime = new FigmaDiagramRuntime();

        var artifact = runtime.FromSource(json);

        Assert.NotNull(artifact);
        Assert.Equal("figma", artifact!.RuntimeId);
        Assert.Equal("Marketing site", artifact.Title);
        Assert.Equal(4, artifact.Elements.Count);
        Assert.Contains(artifact.Elements, e => e.ElementType == "CANVAS" && e.Name == "Home");
        Assert.Contains(artifact.Elements, e => e.ElementType == "FRAME" && e.ParentId == "0:1");
        Assert.Contains(artifact.Elements, e => e.ElementType == "TEXT" && e.ParentId == "1:2");
        Assert.Contains("1 canvas", artifact.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Figma_api_client_sends_token_header_and_returns_parsed_file()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.True(request.Headers.Contains("X-Figma-Token"));
            Assert.Equal("test-token", request.Headers.GetValues("X-Figma-Token").Single());
            Assert.Equal("/v1/files/abc123", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                { "name": "Stub", "document": { "id": "0:0", "type": "DOCUMENT", "name": "Doc" } }
                """, Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.figma.com") };
        var client = new FigmaApiClient(http, new FigmaApiOptions { AccessToken = "test-token" });

        var file = await client.GetFileAsync("abc123");

        Assert.Equal("Stub", file.Name);
        Assert.NotNull(file.Document);
        Assert.Equal("0:0", file.Document!.Id);
    }

    [Fact]
    public async Task Penpot_api_client_posts_to_rpc_command_with_token_header()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/rpc/command/get-file", request.RequestUri!.AbsolutePath);
            Assert.True(request.Headers.Contains("Authorization"));
            Assert.Equal("Token penpot-token", request.Headers.GetValues("Authorization").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                { "id": "00000000-0000-0000-0000-000000000001", "name": "From RPC", "data": null }
                """, Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://design.penpot.app") };
        var client = new PenPotApiClient(http, new PenPotApiOptions { AccessToken = "penpot-token" });

        var file = await client.GetFileAsync(new Guid("00000000-0000-0000-0000-000000000001"));

        Assert.Equal("From RPC", file.Name);
    }

    // ------------------------------------------------------------------ DI wiring

    [Fact]
    public void Diagram_runtimes_register_via_DI_and_resolve_as_collection()
    {
        using var provider = new ServiceCollection()
            .AddConciergeDiagrams()
            .AddConciergePenPotDiagrams(_ => new PenPotApiOptions { AccessToken = "x" })
            .AddConciergeFigmaDiagrams(_ => new FigmaApiOptions { AccessToken = "y" })
            .BuildServiceProvider();

        var runtimes = provider.GetServices<IDiagramRuntime>().ToList();

        Assert.Contains(runtimes, r => r.Id == "mermaid");
        Assert.Contains(runtimes, r => r.Id == "penpot");
        Assert.Contains(runtimes, r => r.Id == "figma");
        Assert.Equal(runtimes.Select(r => r.Id).Distinct().Count(), runtimes.Count);
    }

    // ------------------------------------------------------------------ Helpers

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _send;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_send(request, cancellationToken));
    }
}
