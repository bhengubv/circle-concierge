using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class SpatialGamePackageTests
{
    internal const string Room = """
        {"walls":[
          {"a":[-2,-1.5],"b":[2,-1.5],"height":2.6,"openings":[{"kind":"door","u0":1.4,"u1":2.2,"v0":0,"v1":2}]},
          {"a":[2,-1.5],"b":[2,1.5],"height":2.6,"openings":[]},
          {"a":[2,1.5],"b":[-2,1.5],"height":2.6,"openings":[{"kind":"window","u0":1,"u1":2,"v0":0.8,"v1":2}]},
          {"a":[-2,1.5],"b":[-2,-1.5],"height":2.6,"openings":[]}],
          "objects":[
            {"category":"Desk","center":[-0.8,0.4,0],"size":[1,0.8,0.7],"rotY":0},
            {"category":"Lamp","center":[0.6,0.5,-0.5],"size":[0.4,1,0.4],"rotY":0},
            {"category":"Shelf","center":[1,0.7,0.7],"size":[0.7,1.4,0.4],"rotY":0},
            {"category":"Chair","center":[-0.5,0.4,0.7],"size":[0.5,0.8,0.5],"rotY":0}]}
        """;

    [Fact]
    public void Produces_playable_self_contained_draft_with_verified_manifest()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "spatial-game-" + Guid.NewGuid().ToString("N"));
        try
        {
            var document = RoomCaptureImport.Into(DesignDocument.Blank(), Room, "Studio").Document;
            var path = SpatialGamePackage.Create(document, "Signal Hunt", folder);
            using var archive = ZipFile.OpenRead(path);
            var manifest = JsonDocument.Parse(Read(archive, "manifest.json")).RootElement;
            Assert.Equal("spatial-game-draft", manifest.GetProperty("kind").GetString());
            Assert.False(manifest.GetProperty("published").GetBoolean());
            Assert.False(manifest.GetProperty("rightsConfirmed").GetBoolean());
            Assert.Equal(archive.Entries.Count - 1, manifest.GetProperty("files").GetArrayLength());
            foreach (var file in manifest.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("path").GetString()!;
                var bytes = Read(archive, name);
                Assert.Equal(bytes.Length, file.GetProperty("bytes").GetInt64());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());
            }
            using var scene = JsonDocument.Parse(Read(archive, "scene.json"));
            Assert.Equal(4, scene.RootElement.GetProperty("walls").GetArrayLength());
            Assert.Equal(4, scene.RootElement.GetProperty("objects").GetArrayLength());
            Assert.Equal(3, scene.RootElement.GetProperty("targets").GetArrayLength());
            Assert.Equal(4, scene.RootElement.GetProperty("widthM").GetDouble());
            Assert.DoesNotContain("AppData", System.Text.Encoding.UTF8.GetString(Read(archive, "scene.json")));
            Assert.Contains("from './vendor/three.module.js'", System.Text.Encoding.UTF8.GetString(Read(archive, "game.js")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void Refuses_an_unplayable_room_and_does_not_write_zip()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "spatial-game-" + Guid.NewGuid().ToString("N"));
        var document = RoomCaptureImport.Into(DesignDocument.Blank(), Room, "Studio").Document;
        var frame = Assert.Single(document.Frames);
        foreach (var item in document.ChildrenOf(frame.Id).Where(n => n.Text is "Chair" or "Lamp").ToArray())
            document = document.Remove(item.Id);
        Assert.Throws<InvalidDataException>(() => SpatialGamePackage.Create(document, "Incomplete", folder));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Opens_a_packaged_sample_inside_the_app_without_a_network_url()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "spatial-game-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sample = SpatialGameSample.AddTo(DesignDocument.Blank());
            var path = SpatialGamePackage.Create(sample, "Sample room", folder);
            var html = SpatialGameInlinePreview.FromPackage(path);
            Assert.Contains("/_content/Concierge.Shared.Components/lib/three/three.module.js", html);
            Assert.Contains("data:application/json;base64,", html);
            Assert.Contains("<script type=\"module\">", html);
            Assert.DoesNotContain("src=\"./game.js\"", html);
            Assert.DoesNotContain("http://127.0.0.1", html);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Tool_is_available_only_with_a_local_export_root()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "spatial-game-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new DesignSession();
            session.Record(RoomCaptureImport.Into(DesignDocument.Blank(), Room, "Studio").Document, "Room");
            var workbench = new DesignWorkbench();
            workbench.Attach(session);
            Assert.DoesNotContain(new DesignToolSource(workbench).Tools, tool => tool.Name == "design_package_spatial_game");
            workbench.SpatialGameExportRoot = folder;
            var tool = Assert.Single(new DesignToolSource(workbench).Tools, tool => tool.Name == "design_package_spatial_game");
            var result = await tool.InvokeAsync(new JsonObject { ["title"] = "Signal Hunt" });
            Assert.True(result.Success, result.FailureMessage);
            Assert.Single(Directory.GetFiles(folder, "*.zip"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static byte[] Read(ZipArchive archive, string path)
    {
        using var stream = archive.GetEntry(path)!.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
