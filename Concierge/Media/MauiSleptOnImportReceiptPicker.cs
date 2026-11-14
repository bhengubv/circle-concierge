using Concierge.Shared.Media;

namespace Concierge.Media;

public sealed class MauiSleptOnImportReceiptPicker : ISleptOnImportReceiptPicker
{
    public async Task<SleptOnImportReceipt?> PickAsync(CancellationToken cancellationToken = default)
    {
        var picked = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickAsync(
            new PickOptions { PickerTitle = "Choose the SleptON! import result" }));
        if (picked is null) return null;
        if (!picked.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a SleptON! JSON result file.");
        await using var input = await picked.OpenReadAsync();
        return await SleptOnImportReceiptReader.ReadAsync(input, cancellationToken);
    }
}
