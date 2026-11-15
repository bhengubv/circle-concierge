using System.IO.Compression;
using System.Text.Json.Nodes;
using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class RoomCaptureImportTests
{
    private const string Converted = """
        {
          "walls": [
            {"a":[-2,-1],"b":[2,-1],"height":2.5,
             "openings":[{"kind":"door","u0":1,"u1":1.9,"v0":0,"v1":2.1}]},
            {"a":[2,-1],"b":[2,1],"height":2.5,"openings":[]}
          ],
          "objects":[{"category":"table","center":[0,0.4,0],"size":[1.2,0.8,0.7],"rotY":0.5}]
        }
        """;

    [Fact]
    public void Converted_room_becomes_editable_space_and_retains_measurements()
    {
        var imported = RoomCaptureImport.Into(DesignDocument.Blank(), Converted, "Meeting room");
        Assert.Equal((2, 1, 1), (imported.Walls, imported.Openings, imported.Objects));
        Assert.Equal(DesignMedium.Scene, imported.Document.Medium);
        var room = Assert.Single(imported.Document.Frames);
        Assert.Equal("Meeting room", room.Text);
        var walls = imported.Document.ChildrenOf(room.Id)
            .Where(node => node.Props.TryGetValue("shape", out var shape) && shape == "wall").ToArray();
        Assert.Equal(2, walls.Length);
        Assert.Equal("-200", walls[0].Props["x"]);
        Assert.Equal("200", walls[0].Props["x2"]);
        var opening = Assert.Single(imported.Document.ChildrenOf(walls[0].Id));
        Assert.Equal("100", opening.Props["at"]);
        Assert.Equal("90", opening.Props["width"]);
        var table = Assert.Single(imported.Document.ChildrenOf(room.Id), node => node.Text == "table");
        Assert.Equal("0.5", table.Props["rotation"]);
        Assert.Contains("\"span\":", SceneRenderer.ToHtml(imported.Document));
    }

    [Fact]
    public void Raw_RoomPlan_geometry_and_child_openings_import_without_python()
    {
        var raw = """
            {"walls":[{"identifier":"w1","transform":[1,0,0,0,0,1,0,0,0,0,1,0,0,1.25,0,1],
            "dimensions":[4,2.5,0],"category":{"wall":{} }}],
            "doors":[{"parentIdentifier":"w1","transform":[1,0,0,0,0,1,0,0,0,0,1,0,-0.5,1.05,0,1],
            "dimensions":[0.9,2.1,0],"category":{"door":{"isOpen":false}}}],
            "objects":[]}
            """;
        var imported = RoomCaptureImport.Into(DesignDocument.Blank(), raw, "Room 0");
        Assert.Equal((1, 1, 0), (imported.Walls, imported.Openings, imported.Objects));
        var room = Assert.Single(imported.Document.Frames);
        var wall = Assert.Single(imported.Document.ChildrenOf(room.Id),
            node => node.Props.TryGetValue("shape", out var shape) && shape == "wall");
        var door = Assert.Single(imported.Document.ChildrenOf(wall.Id));
        Assert.Equal("door", door.Props["shape"]);
        Assert.Equal("105", door.Props["at"]);
    }

    [Fact]
    public async Task Zip_import_requires_room_choice_and_is_one_undo_step()
    {
        var path = Path.Combine(Path.GetTempPath(), $"room-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var number in new[] { 0, 1 })
                {
                    using var writer = new StreamWriter(zip.CreateEntry($"sessions/a/rooms/room-{number}.json").Open());
                    writer.Write(Converted);
                }
            }
            var session = new DesignSession();
            var workbench = new DesignWorkbench();
            workbench.Attach(session);
            var tool = new ImportRoomCaptureTool(workbench);
            var ambiguous = await tool.InvokeAsync(new JsonObject { ["path"] = path });
            Assert.False(ambiguous.Success);
            Assert.Equal(0, session.Position);
            var success = await tool.InvokeAsync(new JsonObject { ["path"] = path, ["room"] = 1 });
            Assert.True(success.Success, success.FailureMessage);
            Assert.Equal(1, session.Position);
            Assert.Equal(DesignMedium.Scene, session.Current.Medium);
            session.Back();
            Assert.Equal(DesignMedium.Page, session.Current.Medium);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Malformed_measurements_cannot_change_the_canvas()
    {
        var bad = Converted.Replace("2.5", "9999", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => RoomCaptureImport.Into(DesignDocument.Blank(), bad, "Bad"));
    }

    [Fact]
    public void Local_file_import_is_absent_from_the_hosted_tool_catalogue()
    {
        var workbench = new DesignWorkbench();
        workbench.Attach(new DesignSession());
        var tools = new DesignToolSource(workbench);
        Assert.DoesNotContain(tools.Tools, tool => tool.Name == "design_import_room_scan");
        workbench.AllowLocalFileImports = true;
        Assert.Contains(tools.Tools, tool => tool.Name == "design_import_room_scan");
    }
}
