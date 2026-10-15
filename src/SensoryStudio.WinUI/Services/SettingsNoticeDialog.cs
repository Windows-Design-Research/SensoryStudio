using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.Resources;

namespace SensoryStudio.WinUI.Services;

internal static class SettingsNoticeDialog
{
    public static void Show(SettingsNotice notice)
    {
        var resources = new ResourceLoader();
        string message = string.Format(
            CultureInfo.CurrentCulture,
            resources.GetString($"Settings{notice.Kind}Message"),
            notice.Detail);
        // Settings failures can occur before XamlRoot exists or while the last window closes.
        // A task-modal native dialog remains usable at both lifecycle boundaries.
        if (MessageBox(IntPtr.Zero, message, resources.GetString("SettingsNoticeTitle"), 0x00002030) == 0)
            Trace.TraceError($"Could not display the settings notice. Win32 error: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
