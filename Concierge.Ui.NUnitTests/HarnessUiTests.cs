using Bunit;
using Concierge.Shared.Components.Components;
using Concierge.Shared.Components.State;
using Fluxor;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Concierge.Ui.NUnitTests;

[TestFixture]
public sealed class HarnessUiTests
{
    [Test]
    public async Task Running_conversations_are_projected_into_the_shared_Fluxor_store()
    {
        using var services = new ServiceCollection()
            .AddFluxor(options => options.ScanAssemblies(typeof(RunningConversationsState).Assembly))
            .BuildServiceProvider();
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IStore>();
        await store.InitializeAsync();
        var state = scope.ServiceProvider.GetRequiredService<IState<RunningConversationsState>>();
        var first = Guid.NewGuid();
        scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Dispatch(new RunningConversationsRefreshed([first, first]));

        Assert.That(state.Value.ConversationIds, Is.EquivalentTo(new[] { first }));
    }

    [Test]
    public void Bunit_renders_the_shared_welcome_component_for_both_MAUI_heads()
    {
        using var context = new BunitContext();
        var rendered = context.Render<WelcomePanel>();

        Assert.That(rendered.Find("#welcome-title").TextContent, Is.EqualTo("What would help today?"));
        Assert.That(rendered.Find(".welcome-brand").TextContent, Does.Contain("Concierge"));
    }
}
