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

    public AudiogramView(Context context, string message, bool thinking) : base(context)
    {
        _message = message;
        _thinking = thinking;
        _blue = new Color(context.GetColor(Resource.Color.cm_brand_circle_blue));
        _cyan = new Color(context.GetColor(Resource.Color.cm_brand_circle_spiral_cyan));
        _ink = new Color(context.GetColor(Resource.Color.cm_color_text_primary));
        _surface = new Color(context.GetColor(Resource.Color.cm_color_accent_soft));
        Clickable = true;
        Focusable = true;
        ContentDescription = $"B! voice assistant. {message}";
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
        var r = Math.Min(width, height) * .44f;
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
        _messageScrollDistance = Math.Max(0, _lines.Length - 3) * r * .16f;
        StartMessageScroll();
    }

    private void StartMessageScroll()
    {
        _messageScroll?.Cancel();
        _messageScroll?.Dispose();
        _messageScroll = null;
        _messageOffset = 0;
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
        var r = Math.Min(Width, Height) * .44f;
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
        _paint.Color = _ink;
        _paint.TextAlign = Paint.Align.Center;
        _paint.SetTypeface(_markFace);
        _paint.TextSize = r * .40f;
        canvas.DrawText("B!", x, y - r * .03f, _paint);
        _paint.TextSize = r * .12f;
        _paint.SetTypeface(_bodyFace);
        canvas.Save();
        canvas.ClipRect(x - r * .64f, y + r * .12f, x + r * .64f, y + r * .67f);
        for (var i = 0; i < _lines.Length; i++)
            canvas.DrawText(_lines[i], x, y + r * (.22f + i * .16f) - _messageOffset, _paint);
        canvas.Restore();
    }
}
