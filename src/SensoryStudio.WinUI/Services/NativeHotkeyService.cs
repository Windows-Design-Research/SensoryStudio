using System.Runtime.InteropServices;

namespace SensoryStudio.WinUI.Services;

public sealed class NativeHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int GwlWndProc = -4;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    private readonly Dictionary<int, string> _experienceById = [];
    private readonly WindowProc _windowProc;
    private IntPtr _windowHandle;
    private IntPtr _previousWindowProc;

    public NativeHotkeyService()
    {
        _windowProc = WindowProcedure;
    }

    public event Action<string>? Triggered;

    public void Register(IntPtr windowHandle)
    {
        if (_windowHandle != IntPtr.Zero)
            return;

        _windowHandle = windowHandle;
        _previousWindowProc = SetWindowLongPtr(
            _windowHandle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));

        Register(9005, "zensandart", ModControl | ModShift | ModNoRepeat, 0x45);
    }

    public void Dispose()
    {
        foreach (int id in _experienceById.Keys)
            UnregisterHotKey(_windowHandle, id);
        _experienceById.Clear();

        if (_windowHandle != IntPtr.Zero && _previousWindowProc != IntPtr.Zero)
            SetWindowLongPtr(_windowHandle, GwlWndProc, _previousWindowProc);
        _windowHandle = IntPtr.Zero;
        _previousWindowProc = IntPtr.Zero;
    }

    private void Register(int id, string experienceId, uint modifiers, uint virtualKey)
    {
        if (RegisterHotKey(_windowHandle, id, modifiers, virtualKey))
            _experienceById[id] = experienceId;
    }

    private IntPtr WindowProcedure(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey &&
            _experienceById.TryGetValue(wParam.ToInt32(), out string? experienceId))
        {
            Triggered?.Invoke(experienceId);
            return IntPtr.Zero;
        }

        return CallWindowProc(_previousWindowProc, windowHandle, message, wParam, lParam);
    }

    private delegate IntPtr WindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProc,
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
