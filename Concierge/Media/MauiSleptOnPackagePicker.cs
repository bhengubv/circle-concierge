using Concierge.Shared.Media;

namespace Concierge.Media;

public sealed class MauiSleptOnPackagePicker : ISleptOnPackagePicker
{
    public async Task<SleptOnMediaPackage?> PickAsync(string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var picked = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickAsync(
            new PickOptions { PickerTitle = "Open a prepared SleptON! package" }));
        if (picked is null) return null;
        if (!picked.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a SleptON! ZIP package.");
        await using var input = await picked.OpenReadAsync();
        return await SleptOnPackageLibrary.ImportAsync(input, outputRoot, cancellationToken);
    }
}
