using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Speech;
using Android.Views;
using Android.Widget;
using Concierge.Shared;

namespace Concierge.Wear;

/// <summary>
/// Concierge on the wrist.
///
/// Two screens, and only two — the same pair the Blazor wearable recipe has,
/// for the same reason. A watch is for a glance and a decision, and the arm
/// holding it is usually doing something else:
///
///   1. Something is waiting on you. Allow or Deny, nothing else on screen.
///      This is the thing a watch is genuinely better at than a phone, because
///      it is already on your wrist.
///   2. Otherwise, speak. There is no keyboard worth using at this size.
///
/// What is deliberately absent: the thread list, skills, rooms, settings, the
/// typed composer. Not hidden — never built, because a watch that offers all
/// of it offers none of it well.
///
/// The views are built in code rather than XML. Two screens of a dozen widgets
/// do not earn a resource pipeline, and keeping the colours next to the layout
/// makes them easier to hold against concierge.css, which is the other place
/// this palette is written down.
/// </summary>
[Activity(Label = "Concierge", MainLauncher = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.ScreenLayout)]
public class MainActivity : Activity
{
    // The palette, from concierge.css. Chrome is greyscale; the accent is the
    // only saturated colour, and status is a dot and a word.
    private static readonly Color Ground = Color.ParseColor("#141414");
    private static readonly Color Raised = Color.ParseColor("#1F1F1F");
    private static readonly Color Ink = Color.ParseColor("#EDEDEC");
    private static readonly Color InkSoft = Color.ParseColor("#A3A3A1");
    private static readonly Color InkFaint = Color.ParseColor("#6E6E6C");
    private static readonly Color Accent = Color.ParseColor("#2196F3");
    private static readonly Color Danger = Color.ParseColor("#F85149");
    private static readonly Color Waiting = Color.ParseColor("#D29922");
    private static readonly Color Done = Color.ParseColor("#3FB950");

    private const int SpeechRequest = 1;

    private readonly IConciergeStateService _state = new ConciergeStateService();

    /// <summary>
    /// Which screen, and what has been answered. Held apart from the drawing
    /// because it is the only part of this file that decides anything — and
    /// the only part that can be tested, since bUnit cannot render an Activity.
    /// </summary>
    private readonly WatchFace _face = new();

    /// <summary>The last thing said or heard — a watch reads one answer, not a
    /// transcript.</summary>
    private string _lastHeard = string.Empty;

    private FrameLayout? _root;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _root = new FrameLayout(this);
        _root.SetBackgroundColor(Ground);
        SetContentView(_root);

        Render();
    }

    // ── Which screen ──────────────────────────────────────────────────────

    private void Render()
    {
        if (_root is null)
        {
            return;
        }

        _root.RemoveAllViews();

        var view = _face.Next(_state.GetSnapshot().Approvals);

        _root.AddView(view.Screen == WatchScreen.Decision
            ? BuildDecision(view.Waiting!)
            : BuildSpeak());
    }

    // ── A decision, and nothing else ──────────────────────────────────────

    private View BuildDecision(ApprovalRequest approval)
    {
        var column = Column();

        column.AddView(Eyebrow("NEEDS YOU", RiskColour(approval.Risk)));

        var title = Label(approval.Title, 16f, Ink, bold: true);
        title.SetMaxLines(3);
        title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        column.AddView(title, Spaced(6));

        var reach = Label(ReachOf(approval.Risk), 12f, InkSoft);
        reach.SetMaxLines(2);
        reach.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        column.AddView(reach, Spaced(4));

        // Two controls, side by side, each big enough to hit without looking.
        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        actions.AddView(Pill("Allow", Accent, Color.ParseColor("#0B1218"),
            () => Decide(approval.Id, allowed: true)), Weighted());
        actions.AddView(Pill("Deny", Raised, Ink,
            () => Decide(approval.Id, allowed: false)), Weighted(leftMargin: Dp(6)));
        column.AddView(actions, Spaced(12));

        return column;
    }

    private void Decide(string approvalId, bool allowed)
    {
        _face.Decide(approvalId, allowed);
        Render();
    }

    // ── Speaking ──────────────────────────────────────────────────────────

    private View BuildSpeak()
    {
        var column = Column();

        var said = string.IsNullOrWhiteSpace(_lastHeard) ? "Tap to talk" : _lastHeard;
        var text = Label(said, string.IsNullOrWhiteSpace(_lastHeard) ? 13f : 15f,
                         string.IsNullOrWhiteSpace(_lastHeard) ? InkFaint : Ink);
        text.SetMaxLines(5);
        text.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        column.AddView(text);

        // The microphone is the interface, so it is the biggest thing here.
        // An ImageButton with the same three strokes the web recipe draws: a
        // filled circle with a glyph in it, not a filled circle with a dot in
        // it, which is what a text bullet actually looked like on the watch.
        var mic = new ImageButton(this);
        mic.SetImageResource(Resource.Drawable.ic_mic);
        mic.SetScaleType(ImageView.ScaleType.CenterInside);
        mic.Background = Round(Accent);
        mic.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
        mic.ContentDescription = "Speak";
        mic.Click += (_, _) => Listen();

        var micParams = new LinearLayout.LayoutParams(Dp(64), Dp(64)) { TopMargin = Dp(14) };
        micParams.Gravity = GravityFlags.CenterHorizontal;
        column.AddView(mic, micParams);

        return column;
    }

    private void Listen()
    {
        // Wear OS carries a recogniser, but a stripped image may not. Say so
        // rather than appear to listen and never answer.
        if (!SpeechRecognizer.IsRecognitionAvailable(this))
        {
            _lastHeard = "No speech recogniser on this watch.";
            Render();
            return;
        }

        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        intent.PutExtra(RecognizerIntent.ExtraPrompt, "Speak");

        try
        {
            StartActivityForResult(intent, SpeechRequest);
        }
        catch (ActivityNotFoundException)
        {
            _lastHeard = "No speech recogniser on this watch.";
            Render();
        }
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != SpeechRequest || resultCode != Result.Ok || data is null)
        {
            return;
        }

        var heard = data.GetStringArrayListExtra(RecognizerIntent.ExtraResults);
        if (heard is { Count: > 0 })
        {
            _lastHeard = heard[0] ?? string.Empty;
            Render();
        }
    }

    // ── What a risk level means in reach ──────────────────────────────────

    /// <summary>
    /// Both read from Concierge.Shared, so the watch cannot answer "what can
    /// this do?" differently from the phone. The wording is shared; only the
    /// colour is this head's, because a watch has no stylesheet to name.
    /// </summary>
    private static string ReachOf(string? risk) => ApprovalRisk.ReachOf(risk);


    private static Color RiskColour(string? risk) => ApprovalRisk.SeverityOf(risk) switch
    {
        ApprovalSeverity.Danger => Danger,
        ApprovalSeverity.Caution => Waiting,
        _ => Done
    };

    // ── Building blocks ───────────────────────────────────────────────────

    /// <summary>
    /// The content column, centred and inset.
    ///
    /// Round faces clip their corners, so a square layout inside a circle
    /// loses them: at 192dp the two buttons run to the edges at the widest
    /// part of the face. IsScreenRound is the platform's own answer, and it is
    /// the same decision @media (shape: round) makes in the web recipe.
    /// </summary>
    private LinearLayout Column()
    {
        var column = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        column.SetGravity(GravityFlags.Center);

        var round = Resources?.Configuration?.IsScreenRound ?? false;
        var inset = Dp(round ? 26 : 12);
        column.SetPadding(inset, inset, inset, inset);

        column.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent);

        return column;
    }

    /// <summary>Status is a dot and a word. Never a filled card, never a badge.</summary>
    private LinearLayout Eyebrow(string text, Color dot)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.Center);

        var mark = new View(this) { Background = Round(dot) };
        row.AddView(mark, new LinearLayout.LayoutParams(Dp(6), Dp(6)) { RightMargin = Dp(5) });

        var label = Label(text, 10f, InkFaint);
        label.LetterSpacing = 0.08f;
        row.AddView(label);

        return row;
    }

    private TextView Label(string text, float sizeSp, Color colour, bool bold = false)
    {
        var view = new TextView(this)
        {
            Text = text,
            TextSize = sizeSp,
            TextAlignment = Android.Views.TextAlignment.Center
        };
        view.SetTextColor(colour);
        view.Gravity = GravityFlags.Center;

        if (bold)
        {
            view.SetTypeface(null, TypefaceStyle.Bold);
        }

        return view;
    }

    private Button Pill(string text, Color fill, Color ink, Action onClick)
    {
        var button = new Button(this) { Text = text, TextSize = 13f };
        button.SetTextColor(ink);

        // Android capitalises button text by default. "ALLOW" shouts, and the
        // web recipe says "Allow" — the two heads must read the same.
        button.SetAllCaps(false);
        button.Background = Pill(fill);
        button.SetPadding(0, 0, 0, 0);
        button.Click += (_, _) => onClick();
        return button;
    }

    private Android.Graphics.Drawables.Drawable Round(Color fill)
    {
        var shape = new Android.Graphics.Drawables.GradientDrawable();
        shape.SetShape(Android.Graphics.Drawables.ShapeType.Oval);
        shape.SetColor(fill.ToArgb());
        return shape;
    }

    private Android.Graphics.Drawables.Drawable Pill(Color fill)
    {
        var shape = new Android.Graphics.Drawables.GradientDrawable();
        shape.SetShape(Android.Graphics.Drawables.ShapeType.Rectangle);
        shape.SetCornerRadius(Dp(999));
        shape.SetColor(fill.ToArgb());
        return shape;
    }

    private LinearLayout.LayoutParams Spaced(int topDp) =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(topDp) };

    private LinearLayout.LayoutParams Weighted(int leftMargin = 0) =>
        new(0, Dp(38), 1f) { LeftMargin = leftMargin };

    private int Dp(int value) =>
        (int)(value * (Resources?.DisplayMetrics?.Density ?? 1f));
}
