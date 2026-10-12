namespace Concierge.Shared;

public enum WatchSwipeAction { None, Devices, Back, Yes, No }

public static class WatchInteraction
{
    public static WatchSwipeAction Swipe(float x, float y, bool devices, bool approval)
    {
        if (Math.Abs(x) < 64 || Math.Abs(x) < Math.Abs(y) * 1.5f) return WatchSwipeAction.None;
        if (devices) return x > 0 ? WatchSwipeAction.Back : WatchSwipeAction.None;
        if (approval) return x > 0 ? WatchSwipeAction.Yes : WatchSwipeAction.No;
        return x < 0 ? WatchSwipeAction.Devices : WatchSwipeAction.Back;
    }

    public static bool? ApprovalReply(string text, bool approvalVisible)
        => !approvalVisible ? null : text.Trim().TrimEnd('.', '!', '?').ToLowerInvariant() switch
        {
            "yes" or "allow" => true,
            "no" or "deny" => false,
            _ => null
        };
}
