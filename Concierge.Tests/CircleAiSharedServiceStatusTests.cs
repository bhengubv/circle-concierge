using Bunit;
using Concierge.Shared.Components.Components;
using Concierge.Shared.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class CircleAiSharedServiceStatusTests : BunitContext
{
    [Fact]
    public void Heads_without_a_service_transport_render_no_service_controls()
    {
        var cut = Render<CircleAiSharedServiceStatus>();
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public async Task Successful_catalogue_does_not_claim_model_readiness()
    {
        var service = new Probe(new("com.bhengubv.circleai.service", "0.1", 58,
            true, true, "Catalogue received", [new("chat", "shipping", "Chat interface")]));
        Services.AddSingleton<ICircleAiSharedService>(service);
        var cut = Render<CircleAiSharedServiceStatus>();
        Assert.Equal(0, service.Calls);
        await cut.Find("button.room-sec-head").ClickAsync(new());
        await cut.Find("button.room-btn").ClickAsync(new());
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Catalogue received", cut.Markup);
            Assert.Contains("Unverified", cut.Markup);
            Assert.Contains("shipping", cut.Markup);
            Assert.Contains("58", cut.Markup);
            Assert.DoesNotContain("Ready to chat", cut.Markup);
        });
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Installed_but_unauthorized_service_is_reported_without_a_success_claim()
    {
        Services.AddSingleton<ICircleAiSharedService>(new Probe(new(
            "com.bhengubv.circleai.service", "0.1", 56, true, false,
            "Shared service: not linked for Chat", [])));
        var cut = Render<CircleAiSharedServiceStatus>();
        await cut.Find("button.room-sec-head").ClickAsync(new());
        await cut.Find("button.room-btn").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Contains("not linked for Chat", cut.Markup));
        Assert.Contains("Unverified", cut.Markup);
    }

    private sealed class Probe(CircleAiSharedServiceSnapshot snapshot) : ICircleAiSharedService
    {
        public int Calls { get; private set; }
        public Task<CircleAiSharedServiceSnapshot> InspectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(snapshot);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Linking_requires_a_button_press_and_rechecks_only_after_OS_approval(bool approved)
    {
        var service = new LinkProbe(approved);
        Services.AddSingleton<ICircleAiSharedService>(service);
        var cut = Render<CircleAiSharedServiceStatus>();
        Assert.Equal(0, service.LinkRequests);
        await cut.Find("button.room-sec-head").ClickAsync(new());
        Assert.Empty(cut.FindAll("button").Where(button => button.TextContent == "Connect Circle AI"));
        await cut.Find("button.room-btn").ClickAsync(new());
        Assert.Equal(0, service.LinkRequests);
        await cut.FindAll("button").Single(button => button.TextContent == "Connect Circle AI").ClickAsync(new());
        Assert.Equal(1, service.LinkRequests);
        Assert.Equal(approved ? 2 : 1, service.Inspections);
        Assert.Contains("Unverified", cut.Markup);
        if (approved)
            Assert.DoesNotContain("Connect Circle AI</button>", cut.Markup);
        else
            Assert.Contains("Connection was not approved", cut.Markup);
    }

    private sealed class LinkProbe(bool approved) : ICircleAiSharedService, ICircleAiSharedServiceLink
    {
        private bool _authorized;
        public int LinkRequests { get; private set; }
        public int Inspections { get; private set; }
        public Task<CircleAiSharedServiceSnapshot> InspectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inspections++;
            return Task.FromResult(new CircleAiSharedServiceSnapshot("com.bhengubv.circleai.service", "0.1", 56,
                true, _authorized, _authorized ? "Catalogue received" : "not linked for Chat", []));
        }
        public Task<bool> RequestConnectionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinkRequests++;
            _authorized = approved;
            return Task.FromResult(approved);
        }
    }
}
