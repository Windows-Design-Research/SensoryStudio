using Windows.Graphics;

namespace SensoryStudio.WinUI.Services;

internal static class CompactWindowGeometry
{
    internal static SizeInt32 GetResizeBorderSize(SizeInt32 nativeBorder, double dpiScale)
    {
        int minimum = (int)Math.Ceiling(12 * dpiScale);
        return new SizeInt32(Math.Max(nativeBorder.Width, minimum), Math.Max(nativeBorder.Height, minimum));
    }

    internal static double GetHoverOpacity(
        RectInt32 window, PointInt32 cursor, double dpiScale, bool isOverWindow, bool maximized,
        double hoverMilliseconds)
    {
        int x = cursor.X - window.X;
        int y = cursor.Y - window.Y;
        if (!isOverWindow || x < 0 || y < 0 || x >= window.Width || y >= window.Height)
            return 0;

        double entryOpacity = hoverMilliseconds < 1500 ? 0.18 : 0;
        if (maximized)
            return entryOpacity;

        int distance = Math.Min(Math.Min(x, window.Width - 1 - x), Math.Min(y, window.Height - 1 - y));
        double proximity = 1 - Math.Clamp(distance / (24 * dpiScale), 0, 1);
        return Math.Max(entryOpacity, 0.32 * proximity);
    }

    internal static double FadeHoverOpacity(double current, double target, double elapsedMilliseconds)
    {
        double step = 0.32 * Math.Max(0, elapsedMilliseconds) / 180;
        return target > current ? Math.Min(target, current + step) : Math.Max(target, current - step);
    }

    internal static RectInt32 GetClientBounds(RectInt32 window, RectInt32? maximizedWorkArea)
    {
        if (maximizedWorkArea is not RectInt32 workArea)
            return window;

        int left = Math.Max(window.X, workArea.X);
        int top = Math.Max(window.Y, workArea.Y);
        int right = Math.Min(window.X + window.Width, workArea.X + workArea.Width);
        int bottom = Math.Min(window.Y + window.Height, workArea.Y + workArea.Height);
        return new RectInt32(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    internal static RectInt32[] GetResizeRegions(SizeInt32 window, SizeInt32 border, bool maximized)
    {
        if (maximized || window.Width <= 0 || window.Height <= 0)
            return [];

        int width = Math.Min(border.Width, window.Width / 2);
        int height = Math.Min(border.Height, window.Height / 2);
        // Overlapping horizontal/vertical border regions provide diagonal corner resizing.
        return
        [
            new RectInt32(0, 0, width, window.Height),
            new RectInt32(0, 0, window.Width, height),
            new RectInt32(window.Width - width, 0, width, window.Height),
            new RectInt32(0, window.Height - height, window.Width, height),
        ];
    }

    // Win32 HT* values. Zero leaves hit testing to the existing window procedure.
    internal static int GetResizeHit(RectInt32 window, PointInt32 cursor, SizeInt32 border, bool maximized)
    {
        int x = cursor.X - window.X;
        int y = cursor.Y - window.Y;
        if (maximized || x < 0 || y < 0 || x >= window.Width || y >= window.Height)
            return 0;

        bool left = x < border.Width;
        bool right = x >= window.Width - border.Width;
        bool top = y < border.Height;
        bool bottom = y >= window.Height - border.Height;
        if (top) return left ? 13 : right ? 14 : 12;
        if (bottom) return left ? 16 : right ? 17 : 15;
        return left ? 10 : right ? 11 : 0;
    }
}
