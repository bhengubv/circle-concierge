using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace Concierge.Wear;

/// <summary>Butler's 64 radial bars and B! control, drawn natively for WearOS.</summary>
public sealed class AudiogramView : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly string _message;
    private readonly bool _thinking;
    private readonly bool _conversationActive;
    private readonly string _title;
    private readonly string _deviceStatus;
    private readonly string _notification;
    private readonly bool _hasReply;
    private bool _trackingNotification;
    private bool _notificationTapValid;
    private bool _trackingTranscript;
    private bool _transcriptTapValid;
    private bool _trackingReplay;
    private bool _replayTapValid;
    private bool _messagePaused;
    private float _transcriptDownY;
    private ValueAnimator? _animation;
    private ValueAnimator? _messageScroll;
    private float _phase;
    private float _messageOffset;
    private float _messageScrollDistance;
    private readonly Color _blue, _cyan, _ink, _surface;
    private readonly Typeface _markFace = Typeface.DefaultBold!;
    private readonly Typeface _bodyFace = Typeface.Default!;
    private RadialGradient? _glow;
    private string[] _lines = [];

    public event EventHandler? InboxClick;
    public event EventHandler? ReplayClick;

    public AudiogramView(Context context, string message, bool thinking,
        string title, string deviceStatus, string notification, bool hasReply) : base(context)
    {
        _message = message;
        _thinking = thinking;
        _title = title;
        _deviceStatus = deviceStatus;
        _notification = notification;
        _hasReply = hasReply;
        _conversationActive = thinking
            || (!string.IsNullOrWhiteSpace(message)
                && !message.StartsWith("Tap to talk", StringComparison.OrdinalIgnoreCase));
        _blue = new Color(context.GetColor(Resource.Color.cm_brand_circle_blue));
        _cyan = new Color(context.GetColor(Resource.Color.cm_brand_circle_spiral_cyan));
        _ink = new Color(context.GetColor(Resource.Color.cm_color_text_primary));
        _surface = new Color(context.GetColor(Resource.Color.cm_color_accent_soft));
        Clickable = true;
        Focusable = true;
        LongClickable = true;
        ContentDescription = $"{_title}. {_deviceStatus}. {_notification}. B! voice assistant. {message}"
            + (message.Length > 180 ? " Swipe down over the answer to pause scrolling; tap to resume." : string.Empty)
            + (_hasReply ? " Tap the lower control to hear the latest reply." : string.Empty);
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        if (Android.Provider.Settings.Global.GetFloat(Context!.ContentResolver, "animator_duration_scale", 1f) == 0) return;
        _animation = ValueAnimator.OfFloat(0f, 1f)!;
        _animation.SetDuration(_thinking ? 1400 : 3200);
        _animation.RepeatCount = ValueAnimator.Infinite;
        _animation.Update += (_, e) => { _phase = e.Animation!.AnimatedFraction * MathF.Tau; Invalidate(); };
        _animation.Start();
        StartMessageScroll();
    }

    protected override void OnDetachedFromWindow()
    {
        _animation?.Cancel();
        _animation?.Dispose();
        _animation = null;
        _messageScroll?.Cancel();
        _messageScroll?.Dispose();
        _messageScroll = null;
        _glow?.Dispose();
        _glow = null;
        base.OnDetachedFromWindow();
    }

    protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
    {
        base.OnSizeChanged(width, height, oldWidth, oldHeight);
        var r = RingRadius(width, height);
        _glow?.Dispose();
        _glow = new RadialGradient(width / 2f, height / 2f, r,
            new[] { _surface.ToArgb(), _surface.ToArgb(), Color.Transparent.ToArgb() },
            new[] { 0f, .65f, 1f }, Shader.TileMode.Clamp!);
        _paint.TextSize = r * .12f;
        _paint.SetTypeface(_bodyFace);
        var lines = new List<string>();
        var line = "";
        foreach (var word in _message.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (_paint.MeasureText(next) > r * 1.15f && line.Length > 0) { lines.Add(line); line = word; }
            else line = next;
        }
        if (line.Length > 0) lines.Add(line);
        _lines = lines.ToArray();
        var visibleLines = _conversationActive ? 4 : 3;
        _messageScrollDistance = Math.Max(0, _lines.Length - visibleLines) * r * .15f;
        StartMessageScroll();
    }

    private float RingRadius(int width, int height)
    {
        // Keep animated tick tips within one pixel of the screen edge at peak travel.
        var tickExtension = _thinking ? .0825f : .0575f;
        return Math.Min(width, height) * (.5f / (1f + tickExtension));
    }

    private bool IsNotificationArea(float touchX, float touchY)
    {
        var r = RingRadius(Width, Height);
        var y = Height / 2f;
        var chipWidth = NotificationChipWidth(r);
        return Math.Abs(touchX - Width / 2f) <= chipWidth / 2f + r * .06f
            && touchY >= y - r * .44f
            && touchY <= y - r * .18f;
    }

    private float NotificationChipWidth(float r)
    {
        _paint.SetTypeface(_markFace);
        _paint.TextSize = r * .11f;
        return Math.Min(r * 1.52f, _paint.MeasureText(_notification) + r * .28f);
    }

    private bool IsScrollableAnswerArea(float touchX, float touchY)
    {
        if (_messageScrollDistance <= 0 || !_conversationActive) return false;
        var r = RingRadius(Width, Height);
        return Math.Abs(touchX - Width / 2f) <= r * .64f
            && touchY >= Height / 2f + r * .06f
            && touchY <= Height / 2f + r * .66f;
    }

    private bool IsReplayArea(float touchX, float touchY)
    {
        if (!_hasReply) return false;
        var r = RingRadius(Width, Height);
        return Math.Abs(touchX - Width / 2f) <= r * .52f
            && touchY >= Height / 2f + r * .70f
            && touchY <= Height / 2f + r * .92f;
    }

    public override bool OnTouchEvent(MotionEvent? motion)
    {
        if (motion is null) return false;

        if (motion.Action == MotionEventActions.Down)
        {
            _trackingNotification = IsNotificationArea(motion.GetX(), motion.GetY());
            _notificationTapValid = _trackingNotification;
            if (_trackingNotification) return true;

            _trackingReplay = IsReplayArea(motion.GetX(), motion.GetY());
            _replayTapValid = _trackingReplay;
            if (_trackingReplay) return true;

            _trackingTranscript = IsScrollableAnswerArea(motion.GetX(), motion.GetY());
            _transcriptTapValid = _trackingTranscript;
            _transcriptDownY = motion.GetY();
            if (_trackingTranscript) return true;
        }
        else if (_trackingReplay)
        {
            if (motion.Action == MotionEventActions.Move && !IsReplayArea(motion.GetX(), motion.GetY()))
                _replayTapValid = false;
            else if (motion.Action == MotionEventActions.Up)
            {
                if (_replayTapValid) ReplayClick?.Invoke(this, EventArgs.Empty);
                _trackingReplay = false;
                _replayTapValid = false;
            }
            else if (motion.Action == MotionEventActions.Cancel)
            {
                _trackingReplay = false;
                _replayTapValid = false;
            }

            return true;
        }
        else if (_trackingNotification)
        {
            if (motion.Action == MotionEventActions.Move
                && !IsNotificationArea(motion.GetX(), motion.GetY()))
                _notificationTapValid = false;
            else if (motion.Action == MotionEventActions.Up)
            {
                if (_notificationTapValid) InboxClick?.Invoke(this, EventArgs.Empty);
                _trackingNotification = false;
                _notificationTapValid = false;
            }
            else if (motion.Action == MotionEventActions.Cancel)
            {
                _trackingNotification = false;
                _notificationTapValid = false;
            }

            return true;
        }
        else if (_trackingTranscript)
        {
            var travel = motion.GetY() - _transcriptDownY;
            if (motion.Action == MotionEventActions.Move
                && Math.Abs(travel) > (Resources?.DisplayMetrics?.Density ?? 1f) * 16f)
            {
                _transcriptTapValid = false;
                if (travel > 0 && !_messagePaused)
                {
                    _messageScroll?.Pause();
                    _messagePaused = true;
                    Invalidate();
                }
            }
            else if (motion.Action == MotionEventActions.Up)
            {
                if (_transcriptTapValid && _messagePaused)
                {
                    _messageScroll?.Resume();
                    _messagePaused = false;
                    Invalidate();
                }
                _trackingTranscript = false;
                _transcriptTapValid = false;
            }
            else if (motion.Action == MotionEventActions.Cancel)
            {
                _trackingTranscript = false;
                _transcriptTapValid = false;
            }

            return true;
        }

        return base.OnTouchEvent(motion);
    }

    private void StartMessageScroll()
    {
        _messageScroll?.Cancel();
        _messageScroll?.Dispose();
        _messageScroll = null;
        _messageOffset = 0;
        _messagePaused = false;
        if (_messageScrollDistance <= 0
            || Android.Provider.Settings.Global.GetFloat(Context!.ContentResolver, "animator_duration_scale", 1f) == 0)
            return;

        // Keep three lines readable at a time. The gentle automatic crawl lets the whole
        // answer be read on a meeting table without a tap, swipe, or wrist gesture.
        _messageScroll = ValueAnimator.OfFloat(0f, _messageScrollDistance)!;
        _messageScroll.SetDuration(Math.Max(5000, _lines.Length * 1800L));
        _messageScroll.StartDelay = 1800;
        _messageScroll.RepeatCount = ValueAnimator.Infinite;
        _messageScroll.RepeatMode = ValueAnimatorRepeatMode.Reverse;
        _messageScroll.Update += (_, e) =>
        {
            _messageOffset = (float)e.Animation!.AnimatedValue!;
            Invalidate();
        };
        _messageScroll.Start();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var x = Width / 2f;
        var y = Height / 2f;
        var r = RingRadius(Width, Height);
        _paint.SetStyle(Paint.Style.Fill);
        _paint.SetShader(_glow);
        canvas.DrawCircle(x, y, r, _paint);
        _paint.SetShader(null);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(1f, r * .008f);
        _paint.Color = Color.Argb(100, _blue.R, _blue.G, _blue.B);
        canvas.DrawCircle(x, y, r * .72f, _paint);
        _paint.StrokeWidth = r * .018f;
        _paint.StrokeCap = Paint.Cap.Round;
        for (var i = 0; i < 64; i++)
        {
            var angle = MathF.Tau * i / 64f - MathF.PI / 2;
            var wave = (MathF.Sin(_phase + i * .65f) + 1f) / 2f;
            var length = r * (.035f + wave * (_thinking ? .13f : .08f));
            _paint.Color = i % 4 == 0 ? _cyan : _blue;
            _paint.Alpha = (int)(160 + 95 * wave);
            canvas.DrawLine(x + MathF.Cos(angle) * (r - length / 2), y + MathF.Sin(angle) * (r - length / 2),
                x + MathF.Cos(angle) * (r + length / 2), y + MathF.Sin(angle) * (r + length / 2), _paint);
        }
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Alpha = 255;
        _paint.TextAlign = Paint.Align.Center;
        _paint.SetTypeface(_markFace);
        _paint.Color = _ink;
        _paint.TextSize = r * .15f;
        canvas.DrawText(_title, x, y - r * .61f, _paint);

        _paint.SetTypeface(_bodyFace);
        _paint.TextSize = r * .10f;
        _paint.Color = _ink;
        _paint.Alpha = 185;
        canvas.DrawText(_deviceStatus, x, y - r * .465f, _paint);

        var chipHeight = r * .16f;
        var chipWidth = NotificationChipWidth(r);
        var chipTop = y - r * .42f;
        var chip = new RectF(x - chipWidth / 2f, chipTop,
            x + chipWidth / 2f, chipTop + chipHeight);
        _paint.Alpha = 255;
        _paint.Color = new Color(Context!.GetColor(Resource.Color.cm_brand_circle_blue_strong));
        canvas.DrawRoundRect(chip, chipHeight / 2f, chipHeight / 2f, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(1f, r * .006f);
        _paint.Alpha = 255;
        _paint.Color = _cyan;
        canvas.DrawRoundRect(chip, chipHeight / 2f, chipHeight / 2f, _paint);
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Alpha = 255;
        _paint.Color = _ink;
        _paint.SetTypeface(_markFace);
        _paint.TextSize = r * .11f;
        canvas.DrawText(_notification, x, chipTop + chipHeight / 2f + r * .035f, _paint);

        _paint.Color = _ink;
        _paint.SetTypeface(_markFace);
        _paint.TextSize = r * (_conversationActive ? .22f : .40f);
        canvas.DrawText("B!", x, y + r * (_conversationActive ? -.05f : .15f), _paint);
        _paint.TextSize = r * .12f;
        _paint.SetTypeface(_bodyFace);
        canvas.Save();
        var messageTop = _conversationActive ? .06f : .12f;
        var messageStart = _conversationActive ? .12f : .34f;
        var lineStep = _conversationActive ? .15f : .16f;
        canvas.ClipRect(x - r * .64f, y + r * messageTop, x + r * .64f, y + r * .64f);
        for (var i = 0; i < _lines.Length; i++)
            canvas.DrawText(_lines[i], x, y + r * (messageStart + i * lineStep) - _messageOffset, _paint);
        canvas.Restore();

        if (_messageScrollDistance > 0)
        {
            _paint.Color = _cyan;
            _paint.Alpha = 210;
            _paint.TextSize = r * .075f;
            canvas.DrawText(_messagePaused ? "Paused · tap to resume" : "Swipe down to pause",
                x, y + r * .70f, _paint);
        }

        if (_hasReply)
        {
            _paint.SetStyle(Paint.Style.Fill);
            _paint.Alpha = 220;
            _paint.Color = new Color(Context!.GetColor(Resource.Color.cm_brand_circle_blue_strong));
            var replay = new RectF(x - r * .46f, y + r * .75f, x + r * .46f, y + r * .91f);
            canvas.DrawRoundRect(replay, r * .08f, r * .08f, _paint);
            _paint.SetStyle(Paint.Style.Stroke);
            _paint.StrokeWidth = Math.Max(1f, r * .006f);
            _paint.Alpha = 255;
            _paint.Color = _cyan;
            canvas.DrawRoundRect(replay, r * .08f, r * .08f, _paint);
            _paint.SetStyle(Paint.Style.Fill);
            _paint.Color = _ink;
            _paint.TextSize = r * .085f;
            _paint.SetTypeface(_markFace);
            canvas.DrawText("Tap to listen", x, y + r * .855f, _paint);
        }
    }
}
