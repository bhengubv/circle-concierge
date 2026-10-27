using System.Globalization;

namespace Concierge.Shared.Devices;

/// <summary>Service-owned preparation. The client never chooses or downloads model files.</summary>
public interface ICircleAiServiceSetup
{
    Task StartSetupAsync(CancellationToken cancellationToken);
    Task<CircleAiSetupProgress> ReadSetupProgressAsync(CancellationToken cancellationToken);
    Task CancelSetupAsync(CancellationToken cancellationToken);
}

public sealed record CircleAiSetupProgress(bool Ready, string Status, string? Resource,
    double? Fraction, string? Phase, bool InProgress = false, int? Step = null, int? Steps = null,
    double? RemainingSeconds = null)
{
    public static double? ParseFraction(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) && number is >= 0 and <= 1 ? number : null;
}
