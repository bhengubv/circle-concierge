using Bunit;
using Concierge.Shared;
using Concierge.Shared.Chat;
using Concierge.Shared.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Concierge.Tests;

/// <summary>
/// The pages that were left as shells when the UI was deleted, and are now
/// built: History, Diagrams, Images, Who is chatting, Help, About, Not found,
/// and the key editor inside Settings.
///
/// The fit note from RoomTests applies here too — bUnit has no layout engine,
/// so nothing below knows whether a page fits its window. That is measured on
/// the running app, and it caught three of these: Diagrams ran 43px past the
/// bottom, Images 55px, and the key editor 166px.
/// </summary>
public sealed class ShellPageTests : BunitContext
{
    private void Compose()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"shells-{Guid.NewGuid():N}.db");
        Services.AddLogging();
        Services.AddConciergeCore();

        // History reads the conversation store.
        Services.AddConciergeChat(dbPath);
        Services.AddMudServices();
        Concierge.Shared.Diagrams.ConciergeDiagramsServiceCollectionExtensions.AddConciergeDiagrams(Services);
        Services.AddSingleton<IEnumerable<Concierge.Shared.Media.IImageRuntime>>(
            _ => Array.Empty<Concierge.Shared.Media.IImageRuntime>());
        Services.AddSingleton<IConciergeSecretStore>(new FakeSecretStore());
        JSInterop.Mode = JSRuntimeMode.Loose;

        using var db = Services.GetRequiredService<IDbContextFactory<ConciergeChatDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
    }

    /// <summary>
    /// Every one of them names itself and offers a way back. The layout is
    /// @Body and nothing else, so a page that does not draw its own way out
    /// does not have one — and Not found is the page most likely to be reached
    /// by accident, which is exactly why it needs it.
    /// </summary>
    [Fact]
    public void Every_rebuilt_page_names_itself_and_offers_a_way_back()
    {
        Compose();

        Check<Concierge.Shared.Components.Pages.History>("History");
        Check<Concierge.Shared.Components.Pages.Diagrams>("Diagrams");
        Check<Concierge.Shared.Components.Pages.Images>("Images");
        Check<Concierge.Shared.Components.Pages.SwitchMode>("Who is chatting");
        Check<Concierge.Shared.Components.Pages.Help>("Help");
        Check<Concierge.Shared.Components.Pages.About>("About");
        Check<Concierge.Shared.Components.Pages.NotFound>("Not found");

        void Check<T>(string expected) where T : Microsoft.AspNetCore.Components.IComponent
        {
            var cut = Render<T>();
            Assert.Equal(expected, cut.Find(".room h1").TextContent.Trim());
            Assert.Equal("/", cut.Find("a.room-back").GetAttribute("href"));
        }
    }

    /// <summary>
    /// With no image runtime registered the page says so rather than offering
    /// a button that cannot do anything.
    /// </summary>
    [Fact]
    public void Images_says_so_when_nothing_can_draw()
    {
        Compose();
        var cut = Render<Concierge.Shared.Components.Pages.Images>();

        Assert.Contains("No image runtime is set up", cut.Find(".room").TextContent);
        Assert.Empty(cut.FindAll(".room button.btn-primary"));
    }

    // ── The key editor ────────────────────────────────────────────────────

    /// <summary>
    /// The rule that matters here: a saved key is never rendered back.
    ///
    /// The store can return the value, and putting it into an input would put
    /// it in the DOM, in a screenshot, and in anything that reads the page.
    /// Configured or not is the whole status; re-entering is how you change it.
    /// </summary>
    [Fact]
    public void A_saved_key_is_never_rendered_back_into_the_page()
    {
        Compose();
        var store = (FakeSecretStore)Services.GetRequiredService<IConciergeSecretStore>();
        store.Secrets["Anthropic:ApiKey"] = "sk-ant-do-not-render-me";

        var cut = Render<Concierge.Shared.Components.Components.ApiKeyEditor>();

        // It knows the key is there.
        Assert.Contains("Saved", cut.Markup);

        // And the value appears nowhere in the rendered page.
        Assert.DoesNotContain("sk-ant-do-not-render-me", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// One provider at a time. Three rows each carrying a field ran the
    /// settings panel 166px past the bottom of the window; hiding the others
    /// while one is open is what made it fit.
    /// </summary>
    [Fact]
    public void Only_one_provider_is_open_at_a_time()
    {
        Compose();
        var cut = Render<Concierge.Shared.Components.Components.ApiKeyEditor>();

        Assert.Equal(3, cut.FindAll("button.row").Count);
        Assert.Empty(cut.FindAll("input[type=password]"));

        cut.FindAll("button.row")[0].Click();

        Assert.Single(cut.FindAll("button.row"));
        Assert.Single(cut.FindAll("input[type=password]"));
    }

    /// <summary>
    /// A key field is a password field. Not because anybody is shoulder-surfing
    /// a settings panel, but because the browser must not offer to autofill or
    /// remember it.
    /// </summary>
    [Fact]
    public void The_key_field_does_not_invite_the_browser_to_remember_it()
    {
        Compose();
        var cut = Render<Concierge.Shared.Components.Components.ApiKeyEditor>();
        cut.FindAll("button.row")[0].Click();

        var field = cut.Find("input[type=password]");
        Assert.Equal("off", field.GetAttribute("autocomplete"));
    }

    private sealed class FakeSecretStore : IConciergeSecretStore
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public string Path => "(test)";

        public Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(Secrets);

        public Task SaveAsync(IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default)
        {
            foreach (var pair in secrets)
            {
                Secrets[pair.Key] = pair.Value;
            }

            return Task.CompletedTask;
        }
    }
}
