using Microsoft.UI.Xaml.Controls;

namespace Cue.Services;

/// <summary>Owns the single ContentDialog slot for the application.</summary>
public sealed class DialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ContentDialogResult?> ShowAsync(ContentDialog dialog)
    {
        if (dialog.XamlRoot is null)
            return null;
        await _gate.WaitAsync();
        try
        {
            if (dialog.XamlRoot is null) return null;
            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Best-effort error presentation; never lets presentation failure crash an async UI boundary.</summary>
    public async Task TryShowAsync(ContentDialog dialog)
    {
        try { await ShowAsync(dialog); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"[Cue] Unable to show dialog: {exception}"); }
    }
}
