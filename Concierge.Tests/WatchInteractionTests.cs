using Concierge.Shared;

namespace Concierge.Tests;

public class WatchInteractionTests
{
    [Fact]
    public void A_navigation_swipe_cannot_approve_work()
    {
        Assert.Equal(WatchSwipeAction.Back, WatchInteraction.Swipe(100, 0, false, false));
        Assert.Equal(WatchSwipeAction.Devices, WatchInteraction.Swipe(-100, 0, false, false));
        Assert.Equal(WatchSwipeAction.Back, WatchInteraction.Swipe(100, 0, true, true));
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(100, 100)]
    [InlineData(0, 100)]
    public void Small_or_vertical_movements_cannot_answer_approvals(float x, float y)
        => Assert.Equal(WatchSwipeAction.None, WatchInteraction.Swipe(x, y, false, true));

    [Fact]
    public void Visible_approvals_accept_deliberate_horizontal_swipes()
    {
        Assert.Equal(WatchSwipeAction.Yes, WatchInteraction.Swipe(100, 0, false, true));
        Assert.Equal(WatchSwipeAction.No, WatchInteraction.Swipe(-100, 0, false, true));
    }

    [Fact]
    public void Voice_decisions_require_an_approval_and_a_complete_yes_or_no()
    {
        Assert.Null(WatchInteraction.ApprovalReply("yes", false));
        Assert.Null(WatchInteraction.ApprovalReply("yes but wait", true));
        Assert.True(WatchInteraction.ApprovalReply("Yes!", true));
        Assert.False(WatchInteraction.ApprovalReply("no", true));
    }
}
