// Code-behind for Home. The markup this came from was deleted: none of the
// screens looked anything like the design they are meant to look like, so the
// UI is being rebuilt rather than edited. This is the logic that survived.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;


namespace Concierge.Shared.Components.Pages;

public partial class Home
{

    // ── Reading the snapshot ──────────────────────────────────────────────
    // Risk arrives as a free string ("High", "Medium"), so every one of these
    // falls through to the calmest option rather than throwing or showing a
    // colour it cannot justify. An unrecognised risk is not an emergency.

    private static int RiskWeight(string risk) => risk?.ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    private static string RiskClass(string risk) => risk?.ToLowerInvariant() switch
    {
        "high" => "risk-high",
        "medium" => "risk-medium",
        _ => "risk-low",
    };

    private static string RiskPill(string risk) => risk?.ToLowerInvariant() switch
    {
        "high" => "pill-danger",
        "medium" => "pill-waiting",
        _ => "pill-queued",
    };

    private static string RiskDot(string risk) => risk?.ToLowerInvariant() switch
    {
        "high" => "dot-failed",
        "medium" => "dot-waiting",
        _ => "dot-queued",
    };

    private static string StateDot(string state) => state?.ToLowerInvariant() switch
    {
        "running" => "dot-done",
        "ready" => "dot-queued",
        "failed" => "dot-failed",
        "blocked" => "dot-failed",
        _ => "dot-waiting",
    };

    // "3m", "18m", "2h", then the date. Anything older than a day stops being
    // a duration and becomes a fact about when.
    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at.ToUniversalTime();
        if (span < TimeSpan.Zero) return "just now";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        return at.ToLocalTime().ToString("MMM d");
    }

    private string _typed = string.Empty;
    private string _bellState = "idle";
    private bool _micActive;
    private bool _composerFocused;
    private bool _sending;
    private bool _showOnboarding;
    // The tiles it referred to are gone. The promise is the part worth keeping.
    private string _hint = "I won't do anything without asking you first.";
    private string _greeting = "Hi! What shall we do?";

    private void OnComposerFocus()
    {
        _composerFocused = true;
        // Bell leans forward (listening pose) when the user shows intent,
        // even before they speak or type. Same shape as actual listening
        // but driven by focus, not the mic — the user feels SEEN.
        if (!_micActive) _bellState = "listening";
    }

    private void OnComposerBlur()
    {
        _composerFocused = false;
        if (!_micActive) _bellState = "idle";
    }

    private bool CanSend => !_sending && !string.IsNullOrWhiteSpace(_typed);

    protected override void OnInitialized()
    {
        // Time-of-day greeting — adds personality without being chatty.
        var hour = DateTime.Now.Hour;
        _greeting = hour switch
        {
            >= 5  and < 12 => "Good morning! What shall we do?",
            >= 12 and < 17 => "Hi there! What shall we do?",
            >= 17 and < 22 => "Good evening! What shall we do?",
            _              => "Hi! Up late? What shall we do?",
        };
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // First-launch sequence runs once per device. The flag lives in
            // localStorage so it survives reloads but not full reinstalls.
            try
            {
                var done = await JS.InvokeAsync<string?>("localStorage.getItem", "concierge-onboarded");
                if (string.IsNullOrEmpty(done))
                {
                    _showOnboarding = true;
                    StateHasChanged();
                }
            }
            catch { /* SSR or storage disabled — just skip onboarding silently */ }
        }
    }

    private void HideOnboarding()
    {
        _showOnboarding = false;
    }

    private void PrefillAndFocus(string text)
    {
        _typed = text;
        _hint = "Tap the arrow to send, or change the wording first.";
    }

    private async Task ToggleMic()
    {
        _micActive = !_micActive;
        _bellState = _micActive ? "listening" : "idle";
        _hint = _micActive ? "Listening — tap the mic again to stop." : "Recording stopped.";
        try
        {
            if (_micActive)
            {
                // Fire-and-forget sound/haptic — they're decoration, must
                // not block the conciergeVoice.start call. A suspended
                // AudioContext on MIUI can hang the await indefinitely.
                _ = FireMicStartDecoAsync();
                await JS.InvokeVoidAsync("conciergeVoice.start");
            }
            else
            {
                var base64 = await JS.InvokeAsync<string?>("conciergeVoice.stop");
                if (!string.IsNullOrEmpty(base64))
                {
                    _hint = "Got it. Sending…";
                    // Navigate first — decorations are fire-and-forget.
                    Nav.NavigateTo("/chat?voice=1");
                    _ = FireSendEffectsAsync();
                }
            }
        }
        catch
        {
            _hint = "Couldn't reach the microphone. You can type instead.";
            _micActive = false;
            _bellState = "idle";
            _ = FireHapticAsync("hapticAttention");
        }
    }

    private async Task OpenCamera()
    {
        // Haptic is decoration — fire-and-forget so a hung AudioContext
        // on MIUI can't block the camera open.
        _ = FireHapticAsync("hapticTapLight");
        _hint = "Opening the camera…";
        string? captured = null;
        try
        {
            captured = await JS.InvokeAsync<string?>("conciergeCamera.open");
        }
        catch
        {
            _hint = "I couldn't reach the camera. You can try typing what you wanted to show me instead.";
            try { await JS.InvokeVoidAsync("conciergeSense.hapticAttention"); } catch { }
            return;
        }
        if (string.IsNullOrEmpty(captured))
        {
            _hint = "No picture taken.";
            return;
        }
        // Hand the image to chat via sessionStorage — query-string is too
        // small for a JPEG payload.
        try
        {
            await JS.InvokeVoidAsync("sessionStorage.setItem", "concierge-pending-image", captured);
        }
        catch { }
        _hint = "Got it. Sending the picture to Bell…";
        Nav.NavigateTo("/chat?image=1");
    }

    private string SendHref => CanSend
        ? $"/chat?q={Uri.EscapeDataString(_typed)}"
        : "javascript:void(0)";

    private void HandleSendSideEffects()
    {
        // The anchor href handles navigation. This handler only runs the
        // side-effects: bell animation, fire-and-forget sound/haptic.
        if (!CanSend) return;
        _sending = true;
        _bellState = "thinking";
        _ = FireSendEffectsAsync();
    }

    // Kept for the OnComposerKeyDown Enter handler — uses Nav.NavigateTo
    // since the keyboard path is desktop-only (real touch users tap the
    // anchor above). Enter on input fires a synchronous keydown event,
    // and Nav.NavigateTo works fine there because it's not the
    // touch-pointer path that MAUI WebView munges.
    private void HandleSend()
    {
        if (!CanSend) return;
        _sending = true;
        _bellState = "thinking";
        var prompt = Uri.EscapeDataString(_typed);
        Nav.NavigateTo($"/chat?q={prompt}");
        _ = FireSendEffectsAsync();
    }

    private async Task FireSendEffectsAsync()
    {
        try { await JS.InvokeVoidAsync("conciergeSense.playWhoosh"); } catch { }
        try { await JS.InvokeVoidAsync("conciergeSense.hapticSuccess"); } catch { }
    }

    private async Task FireMicStartDecoAsync()
    {
        try { await JS.InvokeVoidAsync("conciergeSense.playTing"); } catch { }
        try { await JS.InvokeVoidAsync("conciergeSense.hapticTapLight"); } catch { }
    }

    private async Task FireHapticAsync(string method)
    {
        try { await JS.InvokeVoidAsync($"conciergeSense.{method}"); } catch { }
    }

    private void OnComposerKeyDown(KeyboardEventArgs e)
    {
        // Enter on the input (without Shift, for future textarea support)
        // submits — desktop convention. Touch users tap the send arrow.
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            HandleSend();
        }
    }

    private void PrefillAndFocusFx(string text)
    {
        PrefillAndFocus(text);
        _ = FireHapticAsync("hapticTapLight");
    }

    private void RunMagicMomentSideEffects()
    {
        // Anchor href owns the nav. This handles the bell animation +
        // hint copy. Bell goes "cheering" before the page even unmounts —
        // tiny burst of joy before chat takes over.
        _bellState = "cheering";
        _hint = "Watch what I do…";
    }

    private void RunMagicMoment()
    {
        _bellState = "cheering";
        _hint = "Watch what I do…";
        // The "magic moment" is a curated 30-second demo: a guided first
        // result that doesn't require the user to think of a question.
        // Wired to /chat with a special demo flag so the chat page can play
        // a scripted intro on the first turn.
        Nav.NavigateTo("/chat?demo=1");
    }
}
