using Concierge.Shared;

namespace Concierge.Tests;

public sealed class ConciergeDataPathsTests
{
    [Fact]
    public void All_new_creations_live_beneath_one_content_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-storage-test", Guid.NewGuid().ToString("N"));
        var paths = new ConciergeDataPaths(root);

        foreach (var path in new[] { paths.Music, paths.Video, paths.Pictures, paths.Documents,
                     paths.NotaDrafts, paths.BookTrailers, paths.SleptOnPackages })
            Assert.StartsWith(paths.Content + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(root, "concierge.db"), paths.ConversationDatabase);
        Assert.Equal(Path.Combine(root, "projects.db"), paths.ProjectDatabase);
    }

    [Fact]
    public void Existing_nota_draft_is_copied_forward_with_its_metadata_and_source_is_preserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-storage-test", Guid.NewGuid().ToString("N"));
        var paths = new ConciergeDataPaths(root);
        var old = Path.Combine(root, "Nota drafts");
        Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, "song.wav"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(old, "song.wav.json"), "{}");

        paths.CopyLegacyNotaDrafts();
        paths.CopyLegacyNotaDrafts();

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(paths.NotaDrafts, "song.wav")));
        Assert.True(File.Exists(Path.Combine(paths.NotaDrafts, "song.wav.json")));
        Assert.True(File.Exists(Path.Combine(old, "song.wav")));
    }
}
