using Concierge.Ai;
using Concierge.Shared.Chat;

namespace Concierge.Tests;

public sealed class CircleAiExecutionDeviceSelectorTests
{
    private readonly CircleAiExecutionDeviceSelector _selector = new();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void Ordinary_turn_stays_on_a_ready_phone_even_when_Desktop_has_more_memory()
    {
        var decision = _selector.Select(
            "Hi, can you help me?", false,
            true, Catalog(_now, 2, 8, Available("conversation")),
            true, Catalog(_now, 32, 16, Available("conversation", "reasoning")),
            _now);

        Assert.Equal(ChatExecutionTarget.LocalDevice, decision.Target);
        Assert.Equal("conversation", decision.RequiredCapability);
    }

    [Fact]
    public void Reasoning_turn_uses_the_more_resourced_ready_head()
    {
        var decision = _selector.Select(
            "Debug this failure step by step.", false,
            true, Catalog(_now, 2, 8, Available("conversation", "reasoning")),
            true, Catalog(_now, 32, 16, Available("conversation", "reasoning")),
            _now);

        Assert.Equal(ChatExecutionTarget.ConnectedDesktop, decision.Target);
        Assert.Equal("reasoning", decision.RequiredCapability);
    }

    [Fact]
    public void Specialist_turn_uses_the_only_head_that_advertises_the_required_ability()
    {
        var decision = _selector.Select(
            "Debug this failure step by step.", false,
            true, Catalog(_now, 8, 8, Available("conversation")),
            true, Catalog(_now, 2, 4, Available("conversation", "reasoning")),
            _now);

        Assert.Equal(ChatExecutionTarget.ConnectedDesktop, decision.Target);
    }

    [Fact]
    public void Stale_Desktop_capabilities_are_not_used_for_device_selection()
    {
        var decision = _selector.Select(
            "Debug this failure step by step.", false,
            true, Catalog(_now, 2, 8, Available("conversation", "reasoning")),
            true, Catalog(_now.AddMinutes(-2), 32, 16, Available("conversation", "reasoning")),
            _now);

        Assert.Equal(ChatExecutionTarget.LocalDevice, decision.Target);
    }

    [Fact]
    public void Image_turn_uses_a_ready_vision_capable_Desktop_when_mobile_lacks_vision()
    {
        var decision = _selector.Select(
            "What is in this picture?", true,
            true, Catalog(_now, 2, 8, Available("conversation")),
            true, Catalog(_now, 16, 12, Available("conversation", "visual-input")),
            _now);

        Assert.Equal(ChatExecutionTarget.ConnectedDesktop, decision.Target);
        Assert.Equal("visual-input", decision.RequiredCapability);
    }

    private static ChatRuntimeCapabilityCatalog Catalog(
        DateTimeOffset observedAt,
        double memoryGb,
        int cores,
        params ChatRuntimeCapability[] capabilities)
        => new(1, "circleai", "3.6.0", observedAt, capabilities,
            new ChatRuntimeResourceSnapshot(memoryGb, 20, cores, "Online", null, observedAt));

    private static ChatRuntimeCapability[] Available(params string[] ids)
        => ids.Select(id => new ChatRuntimeCapability(id, id, ChatRuntimeCapabilityState.Available)).ToArray();
}
