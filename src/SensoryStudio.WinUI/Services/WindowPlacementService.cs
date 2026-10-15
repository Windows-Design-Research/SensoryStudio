using System.Diagnostics;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace SensoryStudio.WinUI.Services;

public static class WindowPlacementService
{
    private const int DefaultMinimumSize = 200;

    public static void RestoreOrApplyDefault(
        AppWindow appWindow,
        string experienceId,
        SizeInt32 defaultSize,
        DisplayArea? preferredDisplay = null,
        int minimumSize = DefaultMinimumSize)
    {
        WindowBounds? saved = AppSettingsService.GetWindowBounds(experienceId);
        RectInt32? savedBounds = GetSavedBounds(saved);
        if (saved != null && savedBounds == null)
            Debug.WriteLine($"[WindowPlacement] Ignoring invalid saved bounds for '{experienceId}'.");

        DisplayArea? savedDisplay = savedBounds is RectInt32 savedRect
            ? DisplayArea.GetFromRect(savedRect, DisplayAreaFallback.None)
            : null;
        DisplayArea display = preferredDisplay ?? savedDisplay ??
            DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        bool preserveSavedPosition = savedDisplay?.DisplayId.Value == display.DisplayId.Value;

        RectInt32 bounds = CalculateBounds(
            savedBounds,
            defaultSize,
            display.WorkArea,
            preserveSavedPosition,
            minimumSize);
        // Moving across monitors can trigger automatic DPI resizing. Apply pixel dimensions afterward.
        appWindow.Move(new PointInt32(bounds.X, bounds.Y));
        appWindow.MoveAndResize(bounds);
    }

    internal static RectInt32? GetSavedBounds(WindowBounds? saved)
    {
        if (saved?.IsValid != true)
        {
            return null;
        }

        int savedLeft = (int)Math.Round(saved.Left);
        int savedTop = (int)Math.Round(saved.Top);
        int savedWidth = (int)Math.Round(saved.Width);
        int savedHeight = (int)Math.Round(saved.Height);
        return new RectInt32(
            savedLeft,
            savedTop,
            savedWidth,
            savedHeight);
    }

    internal static RectInt32 CalculateBounds(
        RectInt32? savedBounds,
        SizeInt32 defaultSize,
        RectInt32 workArea,
        bool preserveSavedPosition,
        int minimumSize = DefaultMinimumSize)
    {
        int minimumWidth = Math.Min(minimumSize, workArea.Width);
        int minimumHeight = Math.Min(minimumSize, workArea.Height);
        int width = Math.Clamp(savedBounds?.Width ?? defaultSize.Width, minimumWidth, workArea.Width);
        int height = Math.Clamp(savedBounds?.Height ?? defaultSize.Height, minimumHeight, workArea.Height);
        int left = workArea.X + ((workArea.Width - width) / 2);
        int top = workArea.Y + ((workArea.Height - height) / 2);
        if (preserveSavedPosition && savedBounds is RectInt32 saved)
        {
            left = Math.Clamp(saved.X, workArea.X, workArea.X + workArea.Width - width);
            top = Math.Clamp(saved.Y, workArea.Y, workArea.Y + workArea.Height - height);
        }

        return new RectInt32(left, top, width, height);
    }

    public static void Save(AppWindow appWindow, string experienceId)
    {
        PointInt32 position = appWindow.Position;
        SizeInt32 size = appWindow.Size;
        AppSettingsService.SetWindowBounds(
            experienceId,
            position.X,
            position.Y,
            size.Width,
            size.Height);
        AppSettingsService.Save();
    }
}
