using Android.Views;
using Android.Widget;
using Concierge.Away;
using Concierge.Shared.Chat;

namespace Concierge.Wear;

public partial class MainActivity
{
    private enum WristPane { Home, Menu, Conversations, Thread, Queue, Status, Devices, Gestures }
    private WristPane _pane;
    private readonly WristInbox _inbox = new();
    private readonly SemaphoreSlim _readInbox = new(1, 1);
    private Guid? _thread;
    private bool _olderHistory;
    private bool _online;
    private int _queuedCount;
    private AwayDeviceCapabilities? _capabilities;
    private string _syncStatus = "Checking Desktop…";
    private ScrollView? _scroll;
    private Java.Lang.Object? _handBridge;
    private Java.Lang.Runnable? _primaryGesture;
    private Java.Lang.Runnable? _dismissGesture;
    private string _handStatus = "Checking hand gestures…";

    private void OpenPane(WristPane pane) { _scroll?.ScrollTo(0, 0); _pane = pane; Render(); _ = RefreshWristAsync(); }
    private void BackToWrist()
    {
        _pane = _pane == WristPane.Thread ? WristPane.Conversations : _pane == WristPane.Menu ? WristPane.Home : WristPane.Menu;
        Render();
    }

    private View BuildWristPane()
    {
        var column = new LinearLayout(this) { Orientation = Orientation.Vertical };
        column.SetPadding(Dp(32), Dp(32), Dp(32), Dp(52));
        column.AddView(Label(_pane switch { WristPane.Thread => "Conversation", WristPane.Menu => "Your Concierge", _ => _pane.ToString() }, 21f, Ink));
        column.AddView(Label(_syncStatus, 10f, _online ? Accent : InkSoft), Spaced(6));
        void Row(string title, Action action)
        {
            var button = Pill(title, Raised, Ink, action);
            button.SetMinHeight(Dp(48)); button.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
            button.ContentDescription = title;
            column.AddView(button, Spaced(6));
        }
        void Text(string text, bool accent = false) => column.AddView(Label(text, 13f, accent ? Accent : Ink), Spaced(8));
        switch (_pane)
        {
            case WristPane.Menu:
                Row($"Conversations · {_inbox.Snapshot.Conversations.Count(_inbox.IsUnread)} new", () => OpenPane(WristPane.Conversations));
                Row($"Queue · {_desk.Waiting}", () => OpenPane(WristPane.Queue));
                Row($"Status · {_waiting.Count} need you", () => OpenPane(WristPane.Status));
                Row("Devices", () => OpenPane(WristPane.Devices));
                Row("Gestures", () => OpenPane(WristPane.Gestures));
                break;
            case WristPane.Conversations:
                Row("New conversation", () => { _hands.ConversationId = Guid.NewGuid(); _thread = _hands.ConversationId; _pane = WristPane.Home; _lastHeard = "New conversation · tap to talk"; Render(); });
                if (_inbox.Snapshot.Conversations.Count == 0) Text(_online ? "No saved conversations yet." : "No history saved on this watch yet.");
                foreach (var conversation in _inbox.Snapshot.Conversations)
                    Row($"{(_inbox.IsUnread(conversation) ? "New · " : "")}{conversation.Title}\n{conversation.Status}", () => { _thread = conversation.Id; _olderHistory = false; OpenPane(WristPane.Thread); });
                break;
            case WristPane.Thread:
                var title = _inbox.Snapshot.Conversations.FirstOrDefault(c => c.Id == _thread)?.Title ?? "Conversation";
                Text(title, true);
                Row("Continue by voice", () => { _hands.ConversationId = _thread; _pane = WristPane.Home; _lastHeard = $"Continuing {title}"; Render(); Listen(); });
                if (_olderHistory) Row("Latest messages", () => { _olderHistory = false; _ = RefreshWristAsync(); });
                var messages = _thread is { } id && _inbox.Snapshot.Threads.TryGetValue(id, out var saved) ? saved : [];
                if (messages.Count == 0) Text("No messages available yet.");
                foreach (var message in messages.Reverse())
                {
                    Text($"{message.Who} · {message.At.ToLocalTime():HH:mm}", true);
                    Text(message.Text);
                }
                if (messages.FirstOrDefault()?.Seq > 0) Row("Earlier messages", () => _ = ReadEarlierAsync());
                break;
            case WristPane.Queue:
                Text(_thinking ? "Sending to Desktop…" : _desk.Waiting > 0 ? "Waiting to reach Desktop" : "No pending delivery", true);
                var pending = _desk.PendingRequests;
                if (pending.Count == 0) Text("Everything has been sent. Answers are in Conversations.");
                foreach (var request in pending) { Text(request.Situation.At.ToLocalTime().ToString("ddd HH:mm"), true); Text(request.Text); }
                foreach (var work in _inbox.Snapshot.Conversations.Where(c => c.Status is "Working" or "Interrupted" or "Needs retry"))
                    Row($"{work.Title}\n{work.Status}", () => { _thread = work.Id; _olderHistory = false; OpenPane(WristPane.Thread); });
                if (pending.Count > 0) Row(_thinking ? "Delivery in progress" : "Retry delivery", () => { if (!_thinking) _ = RefreshWaitingAsync(); });
                break;
            case WristPane.Status:
                Text(_thinking ? "Your request is being processed." : "Desktop work and decisions", true);
                Text(_capabilities?.CanAnswer == true ? "Ready to answer" : "Assistant preparing or unavailable");
                if (_capabilities?.CircleAi?.Activity is { } activity)
                {
                    Text(activity.Description);
                    if (activity.ProgressRatio is { } ratio)
                    {
                        var progress = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Progress = (int)(Math.Clamp(ratio, 0, 1) * 100) };
                        column.AddView(progress, Spaced(8));
                    }
                }
                foreach (var approval in _waiting)
                    Row($"Needs you · {approval.Summary}", () => { _waiting = new[] { approval }.Concat(_waiting.Where(a => a.Id != approval.Id)).ToArray(); _pane = WristPane.Home; Render(); });
                foreach (var conversation in _inbox.Snapshot.Conversations.Take(20))
                    Row($"{(_inbox.IsUnread(conversation) ? "New · " : "")}{conversation.Title}\n{conversation.Status}", () => { _thread = conversation.Id; _olderHistory = false; OpenPane(WristPane.Thread); });
                if (!_online) Text("Cached status. Reconnect to check current work.");
                break;
            case WristPane.Devices:
                foreach (var device in _devices) Text($"{device.Name}\n{device.Platform} · {(device.Connected ? "Connected" : "Offline")}");
                if (_devices.Count == 0) Text("No connected devices found.");
                break;
            case WristPane.Gestures:
                Text(_handStatus, true);
                Text("Double pinch: talk on Home; scroll through the inbox. Wrist turn: go back. Approvals still require an explicit Yes or No.");
                Text("Turn the crown to scroll. Swipe right to go back. Swipe up on Home to open the inbox.");
                Row("Watch gesture settings", () => { try { StartActivity(new Android.Content.Intent("com.google.wear.input.action.GESTURE_INPUT_SETTINGS")); } catch (Android.Content.ActivityNotFoundException) { _handStatus = "Open Gestures in watch Settings"; Render(); } });
                break;
        }
        Row("Home", () => { _pane = WristPane.Home; Render(); });
        _scroll = new ScrollView(this) { FillViewport = true, Focusable = true, FocusableInTouchMode = true };
        _scroll.AddView(column);
        _scroll.LayoutParameters = new Android.Widget.FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        _scroll.RequestFocus();
        return _scroll;
    }

    private async Task RefreshWristAsync(CancellationToken ct = default)
    {
        if (!await _readInbox.WaitAsync(0, ct)) return;
        try
        {
            var conversations = await _desk.ReadConversationsAsync(ct).ConfigureAwait(false);
            var online = conversations is not null;
            var changed = online != _online || conversations is not null && !conversations.SequenceEqual(_inbox.Snapshot.Conversations);
            if (conversations is not null) _inbox.Save(conversations);
            if (online && !_olderHistory && (_thread ?? _hands.ConversationId) is { } id)
            {
                var events = await _desk.ReadRecentConversationAsync(id, ct).ConfigureAwait(false);
                if (events is not null)
                {
                    var messages = WristTimeline.Messages(events);
                    changed |= !_inbox.Snapshot.Threads.TryGetValue(id, out var previous) || !previous.SequenceEqual(messages);
                    _inbox.Save(id: id, messages: messages);
                    if (_pane == WristPane.Thread && _thread == id && !_olderHistory && events.Count > 0) _inbox.MarkRead(id, events.Max(e => e.Seq));
                }
            }
            var capabilities = online ? await _desk.GetCapabilitiesAsync(ct).ConfigureAwait(false) : null;
            var waiting = online ? await _desk.WaitingAsync(ct).ConfigureAwait(false) : _waiting;
            RunOnUiThread(() =>
            {
                changed |= _queuedCount != _desk.Waiting || capabilities?.CanAnswer != _capabilities?.CanAnswer || capabilities?.CircleAi?.Activity != _capabilities?.CircleAi?.Activity || !_waiting.SequenceEqual(waiting) || _syncStatus == "Checking Desktop…";
                _online = online; _waiting = waiting;
                _capabilities = capabilities;
                _queuedCount = _desk.Waiting;
                _syncStatus = online ? "Connected to Desktop" : _inbox.Snapshot.ObservedAt is { } at ? $"Offline · saved {at.ToLocalTime():HH:mm}" : "Desktop unavailable";
                if (_resumed && changed) Render();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RunOnUiThread(() => { _syncStatus = "Could not save history on this watch"; if (_resumed) Render(); });
        }
        finally { _readInbox.Release(); }
    }

    private async Task ReadEarlierAsync()
    {
        if (_thread is not { } id || !_inbox.Snapshot.Threads.TryGetValue(id, out var messages)) return;
        var before = messages.FirstOrDefault()?.Seq ?? 0;
        var events = await _desk.ReadConversationEventsPageAsync(id, Math.Max(-1, before - 101), 100).ConfigureAwait(false);
        var older = WristTimeline.Messages(events.Where(e => e.Seq < before));
        if (older.Count > 0) { _olderHistory = true; _inbox.Save(id: id, messages: older); RunOnUiThread(Render); }
    }

    public override bool OnGenericMotionEvent(MotionEvent? e)
    {
        if (e?.Action == MotionEventActions.Scroll && (e.Source & InputSourceType.RotaryEncoder) != 0 && _scroll is not null)
        {
            _scroll.ScrollBy(0, (int)(-e.GetAxisValue(Axis.Scroll) * Dp(48))); return true;
        }
        return base.OnGenericMotionEvent(e);
    }

    private void HandPrimary()
    {
        if (_pane != WristPane.Home) _scroll?.SmoothScrollBy(0, Dp(110));
        else if (_visibleApproval is not null) Listen(); // Never approve a consequential action accidentally.
        else if (!_thinking) Listen();
    }
    private void HandDismiss()
    {
        if (_pane != WristPane.Home) BackToWrist();
        else if (_showDevices) { _showDevices = false; Render(); }
        else if (_visibleApproval is null) Finish(); // Does not cancel work or silently deny an approval.
    }
    private void StartHandGestures()
    {
        if (_handBridge is not null) return;
        try
        {
            using var type = Java.Lang.Class.ForName("com.concierge.wear.WristGestures");
            _primaryGesture = new Java.Lang.Runnable(HandPrimary);
            _dismissGesture = new Java.Lang.Runnable(HandDismiss);
            using var method = type.GetMethod("start", Java.Lang.Class.ForName("android.app.Activity"), Java.Lang.Class.ForName("java.lang.Runnable"), Java.Lang.Class.ForName("java.lang.Runnable"));
            _handBridge = method!.Invoke(null, this, _primaryGesture, _dismissGesture);
            _handStatus = type.GetField("status")!.Get(_handBridge)?.ToString() ?? "Hand gestures unavailable";
        }
        catch (Exception e) { Android.Util.Log.Warn("ConciergeGestures", e.ToString()); _handStatus = "Hand gestures unavailable · touch and crown work"; }
    }
    private void StopHandGestures()
    {
        if (_handBridge is not null)
        {
            try { _handBridge.Class.GetMethod("stop")!.Invoke(_handBridge); }
            catch (Exception e) { Android.Util.Log.Warn("ConciergeGestures", e.ToString()); }
            _handBridge.Dispose(); _handBridge = null;
        }
        _primaryGesture?.Dispose(); _dismissGesture?.Dispose();
        _primaryGesture = null; _dismissGesture = null;
    }
}
