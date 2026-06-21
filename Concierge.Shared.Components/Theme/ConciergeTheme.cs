using MudBlazor;

namespace Concierge.Shared.Components.Theme;

/// <summary>
/// Locked brand theme for MudBlazor. Three rules:
///   1. Brand palette is fixed — <c>#2196F3</c> (blue), <c>#2c3e50</c>
///      (deep ink), <c>#ffffff</c> (paper). No orange. Status colours
///      (green/amber/red) are signals, not accents.
///   2. Default MudBlazor look is suppressed — minimal radius, hairline
///      borders instead of soft shadows — so the app reads as a flight-deck
///      rather than "another Material app".
///   3. Single theme, no light-mode toggle. Concierge is dark-by-default
///      because every mission-control app the user looks to (Linear, Vercel,
///      Raycast, GitHub mobile) is dark by default.
///
/// Typography is intentionally NOT configured here — the system font stack is
/// applied globally in <c>flight-deck.css</c> on <c>html, body</c>, which
/// cascades through every MudBlazor component without us having to touch the
/// MudBlazor Typography record (which is rev-locked to MudBlazor's own
/// internal type shape and changes between versions).
/// </summary>
public static class ConciergeTheme
{
    public const string BrandBlue  = "#2196F3";
    public const string BrandInk   = "#2c3e50";
    public const string BrandPaper = "#ffffff";

    // Flight-deck status palette. Used sparingly — like jewels, not paint.
    public const string StatusGreen = "#26b050";   // running / healthy
    public const string StatusAmber = "#f5a623";   // waiting / queued
    public const string StatusRed   = "#e50000";   // needs attention
    public const string StatusGrey  = "#687288";   // idle / done

    // Surface tones (the actual canvas, not brand).
    private const string SurfaceBlack    = "#0b0d12";
    private const string SurfacePanel    = "#141821";
    private const string TextPrimary     = "#f3f4f7";
    private const string TextSecondary   = "#9aa3b6";
    private const string Divider         = "#262b39";

    public static readonly MudTheme Dark = new()
    {
        PaletteDark = new PaletteDark
        {
            // Brand identity — the one place blue earns its keep.
            Primary       = BrandBlue,
            PrimaryContrastText = BrandPaper,
            Secondary     = BrandInk,
            SecondaryContrastText = BrandPaper,
            Tertiary      = BrandBlue,

            // Surfaces. Deep black so cards have something to elevate against.
            Background    = SurfaceBlack,
            Surface       = SurfacePanel,
            AppbarBackground = SurfaceBlack,
            AppbarText    = TextPrimary,
            DrawerBackground = SurfaceBlack,
            DrawerText    = TextPrimary,

            // Text.
            TextPrimary   = TextPrimary,
            TextSecondary = TextSecondary,
            TextDisabled  = "#5a637a",

            // Lines.
            LinesDefault  = Divider,
            LinesInputs   = Divider,
            Divider       = Divider,
            TableLines    = Divider,
            TableStriped  = "rgba(255,255,255,0.02)",

            // Action layer (hovers / focus / selected).
            ActionDefault = TextSecondary,
            ActionDisabled = "#3a4150",
            ActionDisabledBackground = "#1a1f2b",
            HoverOpacity  = 0.08,

            // Status semantics — MudBlazor uses these for Alert / Progress / Chip.
            Success = StatusGreen,
            Warning = StatusAmber,
            Error   = StatusRed,
            Info    = BrandBlue,

            // Misc tonal anchors.
            White = BrandPaper,
            Black = SurfaceBlack,
        },
        LayoutProperties = new LayoutProperties
        {
            // Tighter than MudBlazor's default — flight-deck rhythm.
            DefaultBorderRadius = "10px",
            AppbarHeight = "56px",
        },
    };
}
