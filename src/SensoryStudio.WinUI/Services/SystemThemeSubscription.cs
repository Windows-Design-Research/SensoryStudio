using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace SensoryStudio.WinUI.Services;

internal sealed class SystemThemeSubscription : IDisposable
{
    private readonly FrameworkElement _root;
    private readonly UISettings _settings = new();
    private bool _disposed;

    public SystemThemeSubscription(FrameworkElement root)
    {
        _root = root;
        _settings.ColorValuesChanged += ColorValuesChanged;
        ApplyTheme();
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.ColorValuesChanged -= ColorValuesChanged;
    }

    private void ColorValuesChanged(UISettings sender, object args)
    {
        if (!_root.DispatcherQueue.TryEnqueue(ApplyTheme))
            Debug.WriteLine("[Theme] Could not queue the system theme update.");
    }

    private void ApplyTheme()
    {
        if (_disposed)
            return;

        Windows.UI.Color background = _settings.GetColorValue(UIColorType.Background);
        _root.RequestedTheme = background.R + background.G + background.B >= 384
            ? ElementTheme.Light
            : ElementTheme.Dark;
    }
}
