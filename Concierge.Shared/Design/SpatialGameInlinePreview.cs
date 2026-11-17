using System.IO.Compression;
using System.Text;

namespace Concierge.Shared.Design;

/// <summary>Turns the packaged game into a self-contained view for Concierge's own canvas.</summary>
public static class SpatialGameInlinePreview
{
    public static string FromPackage(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        static string Read(ZipArchive archive, string name)
        {
            var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Game draft is missing {name}.");
            if (entry.Length > 5_000_000) throw new InvalidDataException("Game draft is too large to preview.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        var html = Read(zip, "index.html");
        var game = Read(zip, "game.js");
        var scene = Read(zip, "scene.json");
        const string import = "import * as THREE from './vendor/three.module.js';";
        const string entry = "<script type=\"module\" src=\"./game.js\"></script>";
        if (!game.Contains(import, StringComparison.Ordinal) || !html.Contains(entry, StringComparison.Ordinal)
            || !game.Contains("'./scene.json'", StringComparison.Ordinal)
            || game.Contains("</script", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The game draft cannot be opened inside Concierge.");

        // The same Three.js asset that Space already loads in its MAUI WebView.
        game = game.Replace(import,
            "import * as THREE from '/_content/Concierge.Shared.Components/lib/three/three.module.js';",
            StringComparison.Ordinal);
        game = game.Replace("'./scene.json'",
            $"'data:application/json;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(scene))}'",
            StringComparison.Ordinal);
        // The script is repository-owned template code; room content stays base64 data.
        return html.Replace(entry, $"<script type=\"module\">{game}</script>", StringComparison.Ordinal);
    }
}
