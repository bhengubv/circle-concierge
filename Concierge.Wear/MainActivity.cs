using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware;
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
public class MainActivity : Activity, ISensorEventListener
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

    /// <summary>
    /// The machine that does the making.
    ///
    /// **This used to be `new ConciergeStateService()`** — the watch's own fresh, empty copy
    /// of everything. So its approvals queue was always empty, its decisions were held in
    /// memory and reached nobody, and what it heard was drawn on the face and dropped. A
    /// second Concierge that agreed with the first about nothing.
    /// </summary>
    private readonly Concierge.Away.AwayClient _desk = new();

    /// <summary>What is actually waiting, read from the desk rather than invented here.</summary>
    private IReadOnlyList<Concierge.Away.AwayWaiting> _waiting = [];

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

        // Nothing blocks on this. The microphone works before the desk has been found —
        // finding it is what the first sentence does anyway — and a face that sat blank for
        // six seconds on every wake would be worse than one that occasionally says it could
        // not reach home.
        _ = RefreshWaitingAsync();
    }

    // ── Which screen ──────────────────────────────────────────────────────

    private void Render()
    {
        if (_root is null)
        {
            return;
        }

        _root.RemoveAllViews();

        var view = _face.Next([.. _waiting.Select(a =>
            new ApprovalRequest(a.Id.ToString(), a.Tool, a.Risk, a.Summary, a.AskedAt))]);

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
        // Held locally as well, so the face stops offering it the instant it is pressed. The
        // desk is the truth; this is only so the wrist does not sit there looking unanswered
        // while a request crosses the room.
        _face.Decide(approvalId, allowed);
        Render();

        if (Guid.TryParse(approvalId, out var id))
        {
            _ = AnswerAsync(id, allowed);
        }
    }

    private async Task AnswerAsync(Guid id, bool allowed)
    {
        var landed = await _desk.AnswerAsync(id, allowed).ConfigureAwait(false);

        // Said out loud when it did not land. A watch that reported "allowed" for a call
        // that had already given up would be the approvals badge that always said two, on
        // the one screen with room for a single sentence.
        if (!landed)
        {
            RunOnUiThread(() =>
            {
                _lastHeard = "That one had already gone.";
                Render();
            });
        }

        await RefreshWaitingAsync().ConfigureAwait(false);
    }

    /// <summary>Send what was said, and put the answer on the face.</summary>
    private async Task SendAsync(string said)
    {
        var answer = await _desk
            .SayAsync(said, new Concierge.Away.Situation(DateTimeOffset.Now, Motion: Moving(), AmbientLux: Light()))
            .ConfigureAwait(false);

        RunOnUiThread(() =>
        {
            // Whatever came back, in the words it came back in. The one thing this screen
            // must never do is look the same whether it worked or not.
            _lastHeard = answer.Reply ?? (answer.Understood ? answer.What ?? "Done." : "Nothing came back.");
            Render();
        });

        await RefreshWaitingAsync().ConfigureAwait(false);
    }

    /// <summary>Ask the desk what is waiting, and draw it.</summary>
    private async Task RefreshWaitingAsync()
    {
        // Anything said while there was nowhere to send it goes first. A wrist is out of
        // range constantly, and the moment the face comes back on is the moment worth trying
        // again — before anybody has to think about it.
        var sent = await _desk.FlushAsync().ConfigureAwait(false);

        if (sent > 0)
        {
            RunOnUiThread(() =>
            {
                _lastHeard = sent == 1 ? "Sent what was waiting." : $"Sent {sent} that were waiting.";
                Render();
            });
        }

        var waiting = await _desk.WaitingAsync().ConfigureAwait(false);

        RunOnUiThread(() =>
        {
            _waiting = waiting;
            Render();
        });
    }

    // ── Speaking ──────────────────────────────────────────────────────────

    private View BuildSpeak()
    {
        var column = Column();

        // A held sentence is said out loud. A face that looked the same whether something
        // went or is sitting on the wrist unsent would be this product's signature defect on
        // the one screen with room for a single line.
        var held = _desk.Waiting;

        var said = !string.IsNullOrWhiteSpace(_lastHeard) ? _lastHeard
            : held == 1 ? "1 waiting to go"
            : held > 1 ? $"{held} waiting to go"
            : "Tap to talk";
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

        if (heard is { Count: > 0 } && heard[0] is { Length: > 0 } said)
        {
            // Shown first, then sent. A wrist held up mid-sentence should confirm it was
            // understood before anything slower happens — and what happens next takes a
            // network, which on a train takes a while or never.
            _lastHeard = said;
            Render();

            _ = SendAsync(said);
        }
    }

    // ── What the watch knows when you speak into it ───────────────────────

    /// <summary>
    /// How light it is, or null.
    ///
    /// **This is the free half of seeing.** A watch has no camera worth the name, but it
    /// knows whether you are in the dark or outdoors in daylight, and "make it warmer" said
    /// at 6am walking is a different sentence from the same words at midnight at a desk.
    ///
    /// Read once, from the sensor's last value, rather than by subscribing: a reading taken
    /// at the moment somebody speaks is what the sentence was said in, and a listener left
    /// running to be a fraction more accurate costs battery on the one device that has none.
    /// </summary>
    private double? Light() => _lastLux;

    /// <summary>
    /// Still, walking, or nothing at all.
    ///
    /// Android's own answer via the step counter's recent movement is more than this needs;
    /// what is wanted is the difference between a wrist at rest and a wrist in motion, which
    /// the accelerometer settles. Null where there is no accelerometer or nothing has been
    /// read yet — never a guess, because "still" asserted about somebody on a train is worse
    /// than saying nothing.
    /// </summary>
    private string? Moving()
        => _lastMotion;

    /// <summary>What the sensors last said. Null until something has said it.</summary>
    private double? _lastLux;

    private string? _lastMotion;

    private SensorManager? _sensors;

    /// <summary>
    /// Start listening while the face is on, and stop the moment it is not.
    ///
    /// A watch has one small battery and no mains. A sensor left registered while the screen
    /// is off is the classic way to flatten one by lunchtime, and nothing here needs a
    /// reading taken while nobody is looking — the situation that matters is the one the
    /// sentence was said in.
    /// </summary>
    protected override void OnResume()
    {
        base.OnResume();

        _sensors = GetSystemService(SensorService) as SensorManager;

        if (_sensors is null)
        {
            return;
        }

        // Absent sensors are simply not registered for, and their readings stay null. Most
        // watches have an accelerometer; rather fewer have a light sensor.
        foreach (var kind in new[] { SensorType.Light, SensorType.Accelerometer })
        {
            if (_sensors.GetDefaultSensor(kind) is { } sensor)
            {
                _sensors.RegisterListener(this, sensor, SensorDelay.Normal);
            }
        }
    }

    protected override void OnPause()
    {
        _sensors?.UnregisterListener(this);
        base.OnPause();
    }

    public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy)
    {
        // Nothing here is precise enough for accuracy to change the answer: "in the dark"
        // and "walking" survive a poorly calibrated sensor.
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (e?.Sensor is null || e.Values is null || e.Values.Count == 0)
        {
            return;
        }

        if (e.Sensor.Type == SensorType.Light)
        {
            _lastLux = e.Values[0];
            return;
        }

        if (e.Sensor.Type != SensorType.Accelerometer || e.Values.Count < 3)
        {
            return;
        }

        // Gravity alone reads about 9.81 on a still wrist. How far the total is from that is
        // how much the arm is actually doing — which is all this needs, and it needs no
        // filtering to tell a wrist at rest from one swinging.
        var magnitude = Math.Sqrt(
            (e.Values[0] * e.Values[0]) + (e.Values[1] * e.Values[1]) + (e.Values[2] * e.Values[2]));

        var moving = Math.Abs(magnitude - 9.81);

        // Two thresholds and a gap between them, so a wrist hovering at the boundary does not
        // flicker between two words on every reading.
        _lastMotion = moving switch
        {
            < 0.6 => "still",
            > 2.5 => "moving",
            _ => _lastMotion,
        };
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
