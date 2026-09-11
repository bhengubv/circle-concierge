namespace Concierge.Tests;

/// <summary>
/// Nothing this product draws with is fetched from the internet.
///
/// **three.js is carried in this repository for a stated reason** — a design surface that
/// needs a connection to draw a box is not a local-first product — **and diagrams quietly
/// needed one anyway**, pulling mermaid from a CDN every time. On a machine with no internet
/// the screen said "watch it draw" and then printed a module-import error.
///
/// So this checks the rule rather than the one file that broke it: no script this product
/// ships reaches out to a CDN to do its job. It is a grep, and a grep is exactly the right
/// shape here — the failure it guards against is somebody adding a convenient one-line import
/// years from now, which no unit test would ever see.
/// </summary>
public sealed class DrawsWithoutTheInternetTests
{
    private static string Wwwroot
    {
        get
        {
            var here = AppContext.BaseDirectory;

            var root = Path.GetFullPath(Path.Combine(
                here, "..", "..", "..", "..", "Concierge.Shared.Components", "wwwroot"));

            return root;
        }
    }

    private static readonly string[] Cdns =
    [
        "cdn.jsdelivr.net",
        "unpkg.com",
        "cdnjs.cloudflare.com",
        "esm.sh",
        "esm.run",
    ];

    [Fact]
    public void No_script_it_ships_fetches_what_it_draws_with()
    {
        Assert.True(Directory.Exists(Wwwroot), Wwwroot);

        foreach (var script in Directory.EnumerateFiles(Wwwroot, "*.js", SearchOption.TopDirectoryOnly))
        {
            var text = File.ReadAllText(script);

            foreach (var cdn in Cdns)
            {
                Assert.False(
                    text.Contains(cdn, StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(script)} loads something from {cdn}. Carry it in "
                    + "wwwroot/lib instead, the way three.js and mermaid are.");
            }
        }
    }

    /// <summary>
    /// And the two it draws with are actually here. A vendored copy that nobody checked is a
    /// copy that goes missing in a publish profile and takes the feature with it.
    /// </summary>
    [Theory]
    [InlineData("lib/three/three.core.js")]
    [InlineData("lib/mermaid/mermaid.min.js")]
    [InlineData("lib/mermaid/LICENSE")]
    public void What_it_draws_with_is_carried_in_the_project(string file)
    {
        var path = Path.Combine(Wwwroot, file.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"{file} is missing from wwwroot.");
        Assert.True(new FileInfo(path).Length > 0, $"{file} is empty.");
    }

    /// <summary>
    /// Both are MIT, which is the rule this repository is held to for everything it carries.
    /// </summary>
    [Fact]
    public void And_both_are_licensed_the_way_everything_here_has_to_be()
    {
        foreach (var licence in new[] { "lib/three/LICENSE", "lib/mermaid/LICENSE" })
        {
            var path = Path.Combine(Wwwroot, licence.Replace('/', Path.DirectorySeparatorChar));

            Assert.Contains("MIT", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }
}
