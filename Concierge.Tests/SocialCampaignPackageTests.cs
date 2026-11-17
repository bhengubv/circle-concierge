using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class SocialCampaignPackageTests
{
    [Fact]
    public void Packages_copy_and_media_with_exact_hashes_without_leaking_local_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-campaign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "private-image.png");
            File.WriteAllBytes(image, [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82]);
            var first = new SocialPostDraft
            {
                Id = Guid.NewGuid(), Brand = "Concierge", Copy = "Make the idea. Review the draft.",
                DestinationHints = ["Instagram", "LinkedIn"], AltText = "Blue Circle ring on a dark card.",
                MediaPath = image, MediaType = "image/png"
            };
            var second = new SocialPostDraft
            {
                Id = Guid.NewGuid(), Brand = "SleptON!", Copy = "Accept, review, then decide what to add.",
                DestinationHints = ["Facebook"]
            };
            var draft = new SocialCampaignDraft { Title = "Creator campaign", Audience = "Independent creators", Posts = [first, second] };
            var package = SocialCampaignPackage.Create(draft, Path.Combine(root, "exports"), rightsConfirmed: true);
            using var archive = ZipFile.OpenRead(package);
            Assert.Equal(3, archive.Entries.Count);
            var manifest = Read(archive, "manifest.json");
            var posts = Read(archive, "posts.json");
            Assert.DoesNotContain(root, manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(root, posts, StringComparison.OrdinalIgnoreCase);
            using var metadata = JsonDocument.Parse(manifest);
            Assert.Equal("social-campaign", metadata.RootElement.GetProperty("kind").GetString());
            Assert.False(metadata.RootElement.GetProperty("published").GetBoolean());
            var files = metadata.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Assert.Equal(2, files.Length);
            foreach (var file in files)
            {
                var entry = archive.GetEntry(file.GetProperty("path").GetString()!)!;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                Assert.Equal(file.GetProperty("bytes").GetInt64(), buffer.Length);
                Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
            }
            using var postJson = JsonDocument.Parse(posts);
            Assert.Equal(2, postJson.RootElement.GetProperty("posts").GetArrayLength());
            Assert.Equal("media/" + first.Id.ToString("N") + ".png",
                postJson.RootElement.GetProperty("posts")[0].GetProperty("mediaPath").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Refuses_unapproved_or_mislabeled_media_without_writing_a_package()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-campaign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var media = Path.Combine(root, "fake.mp4");
            File.WriteAllBytes(media, new byte[32]);
            var draft = new SocialCampaignDraft
            {
                Title = "Campaign", Audience = "Creators",
                Posts = [new SocialPostDraft { Brand = "Concierge", Copy = "Create and review your work.",
                    DestinationHints = ["Instagram"], MediaPath = media, MediaType = "video/mp4",
                    AltText = "A demonstration of the Creator." }]
            };
            var output = Path.Combine(root, "exports");
            Assert.Throws<InvalidOperationException>(() => SocialCampaignPackage.Create(draft, output, rightsConfirmed: false));
            Assert.Throws<InvalidDataException>(() => SocialCampaignPackage.Create(draft, output, rightsConfirmed: true));
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Read(ZipArchive archive, string path)
    {
        using var input = archive.GetEntry(path)!.Open();
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }
}
