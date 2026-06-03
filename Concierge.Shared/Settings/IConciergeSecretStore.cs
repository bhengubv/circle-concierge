namespace Concierge.Shared.Settings;

/// <summary>
/// Per-machine secret store. Persists API keys + access tokens to a local file outside the
/// repo so they don't leak via source control, and reads them back at host startup. The
/// host's configuration pipeline layers this store on top of <c>appsettings.json</c> /
/// environment variables — values here win when present.
/// </summary>
public interface IConciergeSecretStore
{
    /// <summary>Absolute path of the persisted secrets file (for the "Restart required" banner).</summary>
    string Path { get; }

    /// <summary>Reads every saved secret as a flat dictionary (key = colon-delimited config key).</summary>
    Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the supplied secrets, merging with whatever is already on disk. Empty values
    /// remove the key. The write is atomic (temp + rename) so a crash mid-save leaves the
    /// previous file intact.
    /// </summary>
    Task SaveAsync(IReadOnlyDictionary<string, string> secrets, CancellationToken cancellationToken = default);
}

/// <summary>
/// Catalogue of secrets the host knows how to read. The Settings UI walks this list to
/// produce one input per row; the host wires the same key names in <c>IConfiguration</c>
/// so a saved secret takes effect on next start.
/// </summary>
public static class ConciergeSecretKeys
{
    public const string OpenAi = "OpenAI:ApiKey";
    public const string OpenAiImages = "OpenAIImages:ApiKey";
    public const string OpenAiVoice = "OpenAIVoice:ApiKey";
    public const string Anthropic = "Anthropic:ApiKey";
    public const string Gemini = "Gemini:ApiKey";
    public const string Stability = "Stability:ApiKey";
    public const string PenPot = "PenPot:AccessToken";
    public const string Figma = "Figma:AccessToken";
    public const string ConciergeApiKey = "Auth:ApiKey";

    /// <summary>
    /// Display rows the Settings UI iterates. Order matches the Setting page layout: chat
    /// providers first, then images, voice, design tools, then the auth gate.
    /// </summary>
    public static IReadOnlyList<(string Key, string Label, string Hint)> Catalog { get; } = new[]
    {
        (OpenAi,         "OpenAI · chat",   "gpt-4o-mini chat completions."),
        (Anthropic,      "Anthropic",       "Claude 3.5 Sonnet messages API."),
        (Gemini,         "Google Gemini",   "Gemini 2.0 Flash streamGenerateContent."),
        (OpenAiImages,   "OpenAI · images", "DALL-E 3 image generation."),
        (Stability,      "Stability AI",    "Stable Diffusion 3.5 generation."),
        (OpenAiVoice,    "OpenAI · voice",  "Whisper transcription + TTS synthesis."),
        (PenPot,         "PenPot",          "Personal access token from PenPot settings."),
        (Figma,          "Figma",           "Personal access token from Figma settings."),
        (ConciergeApiKey,"Concierge gate",  "Optional X-Concierge-Key header — protects the Web host."),
    };
}
