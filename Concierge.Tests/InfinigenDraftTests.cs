using Concierge.Shared.Design;

namespace Concierge.Tests;

public sealed class InfinigenDraftTests
{
    [Fact]
    public async Task Missing_generator_is_not_advertised_and_cannot_create_a_draft()
    {
        var root = Path.Combine(Path.GetTempPath(), "concierge-infinigen-" + Guid.NewGuid().ToString("N"));
        var drafts = new InfinigenDrafts(root, Path.Combine(root, "not-installed.exe"));

        Assert.False(drafts.IsAvailable);
        Assert.Contains("needs Infinigen", drafts.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => drafts.CreateSofaAsync(0));
        Assert.False(Directory.Exists(root));
    }
}
