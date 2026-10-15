using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using SensoryStudio.WinUI.Services;
using Windows.Graphics;

namespace SensoryStudio.WinUI.Experiences;

internal sealed class ResizeBorderCue : IDisposable
{
    private readonly IntPtr _windowHandle;
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private readonly Action<double> _setOpacity;
    private TimeSpan _lastTick;
    private TimeSpan? _pointerEnteredAt;
    private double _intensity;
    private bool _reportedBoundsFailure;
    private bool _disposed;

    public ResizeBorderCue(
        IntPtr windowHandle, DispatcherQueue dispatcherQueue, Action<double> setOpacity)
    {
        _windowHandle = windowHandle;
        _setOpacity = setOpacity;

        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += Timer_Tick;
        _timer.Start();
        _setOpacity(0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _setOpacity(0);
    }

    private void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        TimeSpan elapsed = _lifetime.Elapsed;
        double deltaMilliseconds = (elapsed - _lastTick).TotalMilliseconds;
        _lastTick = elapsed;

        double next = CompactWindowGeometry.FadeHoverOpacity(
            _intensity, GetTargetOpacity(elapsed), deltaMilliseconds);
        if (next == _intensity)
            return;
        _intensity = next;
        _setOpacity(next);
    }

    private double GetTargetOpacity(TimeSpan elapsed)
    {
        if (!IsWindowVisible(_windowHandle) || IsIconic(_windowHandle))
        {
            _pointerEnteredAt = null;
            return 0;
        }

        if (!GetCursorPos(out Point cursor) ||
            !GetClientRect(_windowHandle, out WindowRect client))
        {
            if (!_reportedBoundsFailure)
                Debug.WriteLine($"[ResizeBorderCue] Could not read pointer/window bounds: {Marshal.GetLastWin32Error()}");
            _reportedBoundsFailure = true;
            _pointerEnteredAt = null;
            return 0;
        }
        _reportedBoundsFailure = false;

        // Native sampling also covers resize handles and hosted WebView2 content.
        bool isOverWindow = GetAncestor(WindowFromPoint(cursor), 2) == _windowHandle;
        if (!ScreenToClient(_windowHandle, ref cursor))
        {
            Debug.WriteLine("[ResizeBorderCue] Could not map the pointer to client coordinates.");
            _pointerEnteredAt = null;
            return 0;
        }
        isOverWindow &= cursor.X >= client.Left && cursor.X < client.Right &&
            cursor.Y >= client.Top && cursor.Y < client.Bottom;
        if (isOverWindow)
            _pointerEnteredAt ??= elapsed;
        else
            _pointerEnteredAt = null;

        return CompactWindowGeometry.GetHoverOpacity(
            new RectInt32(client.Left, client.Top, client.Right - client.Left, client.Bottom - client.Top),
            new PointInt32(cursor.X, cursor.Y),
            GetDpiForWindow(_windowHandle) / 96d,
            isOverWindow,
            IsZoomed(_windowHandle),
            (elapsed - (_pointerEnteredAt ?? elapsed)).TotalMilliseconds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);
}