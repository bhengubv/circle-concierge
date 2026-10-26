namespace Concierge.Shared.Devices;

public interface IDesktopSummon
{
    bool Enabled { get; }
    string Shortcut { get; }
    string Status { get; }
    event Action? Summoned;
    Task SetEnabledAsync(bool enabled);
    Task SetShortcutAsync(string shortcut);
}
