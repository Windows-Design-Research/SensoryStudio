using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SensoryStudio.WinUI.Services;
using Windows.Graphics;

namespace SensoryStudio.WinUI.Experiences;

public sealed partial class CompactExperienceWindow : Window
{
    private const int CompactControlsHeight = 72;
    private const int MinimumDialogHostWidth = 420;
    private const int MinimumDialogHostHeight = 300;
    private const double CollapsedPillWidth = 40;
    private const double ExpandedPillWidth = 170;
    private const double ExpandedPillHeight = 56;
    private const double CollapsedPillHeight =
        ExpandedPillHeight * CollapsedPillWidth / ExpandedPillWidth;
    private const double CollapsedPillOffset =
        (ExpandedPillHeight - CollapsedPillHeight) / 2;
    private const int DwmBbEnable = 0x1;
    private const int DwmBbBlurRegion = 0x2;
    private const int DwmWaBorderColor = 34;
    private const int GwlWndProc = -4;
    private const int DefaultMinimumWindowSize = 200;
    private const int ZenGardenMinimumWindowSize = 400;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmDwmCompositionChanged = 0x031E;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmNcPaint = 0x0085;
    private const uint WmNcActivate = 0x0086;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmSetCursor = 0x0020;
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;
    private const int BlackBrush = 4;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private readonly IExperience _experience;
    private readonly string _experienceId;
    private readonly MainWindow _owner;
    private readonly Action<IExperience> _restore;
    private readonly DispatcherQueueTimer _controlsTimer;
    private readonly ResizeBorderCue? _resizeBorderCue;
    private readonly InputNonClientPointerSource? _resizeInput;
    private readonly bool _isZenGarden;
    private readonly bool _hasRoundedWindow;
    private readonly WindowProc _windowProc;
    private readonly SystemThemeSubscription _themeSubscription;
    private Storyboard? _controlsTransition;
    private IntPtr _previousWindowProcedure;
    private bool _controlsVisible;
    private bool _returningToMain;

    public CompactExperienceWindow(
        IExperience experience,
        string experienceId,
        MainWindow owner,
        Action<IExperience> restore)
    {
        _experience = experience;
        _experienceId = experienceId;
        _owner = owner;
        _restore = restore;
        _windowProc = WindowProcedure;
        InitializeComponent();
        _themeSubscription = new SystemThemeSubscription(Root);
        Title = experience.Title;
        Title = experience.Title;
        ExperiencePresenter.Content = experience.View;
        _isZenGarden = experience is ZenGardenExperience;
        _hasRoundedWindow = _isZenGarden;
        Activated += CompactExperienceWindow_Activated;
        if (experience is ZenGardenExperience garden)
            garden.SetHostWindow(this, ControlsPill);
        SelectDuration(owner.SelectedDurationMinutes);
        UpdateControls();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ControlsPill);

        _controlsTimer = DispatcherQueue.CreateTimer();
        _controlsTimer.Interval = TimeSpan.FromMilliseconds(16);
        _controlsTimer.Tick += (_, _) => UpdateControls();
        _controlsTimer.Start();

        AppWindow appWindow = GetAppWindow();
        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _previousWindowProcedure = SetWindowLongPtr(
            windowHandle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
        DisplayArea ownerDisplay = DisplayArea.GetFromWindowId(
            owner.AppWindow.Id,
            DisplayAreaFallback.Primary);
        WindowPlacementService.RestoreOrApplyDefault(
            appWindow,
            experienceId,
            GetInitialSize(experience),
            ownerDisplay,
            _isZenGarden ? ZenGardenMinimumWindowSize : DefaultMinimumWindowSize);
        appWindow.IsShownInSwitchers = false;
        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            // Keep the resize frame capability; our non-client handlers hide its paint.
            presenter.SetBorderAndTitleBar(!_hasRoundedWindow, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        _resizeInput = InputNonClientPointerSource.GetForWindowId(appWindow.Id);
        Root.SizeChanged += Root_SizeChanged;
        appWindow.Closing += AppWindow_Closing;
        appWindow.Changed += AppWindow_Changed;
        ApplyWindowTransparency();
        ClearInitialBackground(windowHandle);
        SystemBackdrop = new TransparentSystemBackdrop();
        _resizeBorderCue = new ResizeBorderCue(
            windowHandle,
            DispatcherQueue,
            opacity => WindowBoundaryCue.Opacity = opacity);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPresenterChange)
            ApplyWindowRegion(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        RefreshResizeInputRegions(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void CompactExperienceWindow_Activated(
        object sender,
        WindowActivatedEventArgs args)
    {
        if (_hasRoundedWindow &&
            args.WindowActivationState != WindowActivationState.Deactivated)
        {
            ApplyWindowRegion(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public int Flags;
        public int Enable;
        public IntPtr BlurRegion;
        public int TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowRect Monitor;
        public WindowRect WorkArea;
        public uint Flags;
    }

    private delegate IntPtr WindowProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr windowHandle, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(
        IntPtr windowHandle,
        ref DwmBlurBehind blurBehind);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref uint value,
        int valueSize);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out WindowRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr windowHandle, out WindowRect rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectIndex);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr deviceContext, ref WindowRect rect, IntPtr brush);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr windowHandle, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProcedure,
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref WindowRect rect, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    private void ApplyWindowTransparency()
    {
        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint borderColor = DwmColorNone;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(
            windowHandle,
            DwmWaBorderColor,
            ref borderColor,
            sizeof(uint)));

        var margins = new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(windowHandle, ref margins));

        IntPtr region = CreateRectRgn(-2, -2, -1, -1);
        if (region == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the transparent window region.");

        try
        {
            var blurBehind = new DwmBlurBehind
            {
                Flags = DwmBbEnable | DwmBbBlurRegion,
                Enable = 1,
                BlurRegion = region,
            };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(windowHandle, ref blurBehind));
        }
        finally
        {
            DeleteObject(region);
        }

        if (!_hasRoundedWindow)
        {
            // Recalculate the non-client area without moving, resizing, or activating.
            const uint frameChanged = 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010;
            if (!SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, 0, 0, frameChanged))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        ApplyWindowRegion(windowHandle);
    }

    private void ApplyWindowRegion(IntPtr windowHandle)
    {
        if (!GetWindowRect(windowHandle, out WindowRect rect))
            throw new InvalidOperationException("Could not read the compact window bounds.");

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        bool isMaximized = GetAppWindow().Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Maximized;
        var cornerRadius = new CornerRadius(_hasRoundedWindow && !isMaximized ? 8 : 0);
        WindowBoundaryOutline.CornerRadius = cornerRadius;
        WindowBoundaryGlow.CornerRadius = cornerRadius;
        int cornerDiameter = (int)Math.Round(16 * GetDpiForWindow(windowHandle) / 96d);
        IntPtr region = _hasRoundedWindow && !isMaximized
            ? CreateRoundRectRgn(0, 0, width + 1, height + 1, cornerDiameter, cornerDiameter)
            : CreateRectRgn(0, 0, width, height);
        if (region == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the compact window region.");

        if (SetWindowRgn(windowHandle, region, true) == 0)
        {
            DeleteObject(region);
            throw new InvalidOperationException("Could not apply the compact window region.");
        }

        RefreshResizeInputRegions(windowHandle);
    }

    private void RefreshResizeInputRegions(IntPtr windowHandle)
    {
        if (_resizeInput is null)
            return;

        if (!GetClientRect(windowHandle, out WindowRect client))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        UpdateResizeInputRegions(
            new SizeInt32(client.Right - client.Left, client.Bottom - client.Top),
            GetResizeBorderSize(windowHandle),
            IsZoomed(windowHandle));
    }

    private void UpdateResizeInputRegions(SizeInt32 windowSize, SizeInt32 borderSize, bool maximized)
    {
        if (_resizeInput is null)
            return;

        RectInt32[] regions = CompactWindowGeometry.GetResizeRegions(windowSize, borderSize, maximized);
        NonClientRegionKind[] kinds =
        [
            NonClientRegionKind.LeftBorder, NonClientRegionKind.TopBorder,
            NonClientRegionKind.RightBorder, NonClientRegionKind.BottomBorder,
        ];
        for (int i = 0; i < kinds.Length; i++)
        {
            if (regions.Length == 0)
                _resizeInput.ClearRegionRects(kinds[i]);
            else
                _resizeInput.SetRegionRects(kinds[i], [regions[i]]);
        }
    }

    private static SizeInt32 GetResizeBorderSize(IntPtr windowHandle)
    {
        uint dpi = GetDpiForWindow(windowHandle);
        int padding = GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
        return CompactWindowGeometry.GetResizeBorderSize(
            new SizeInt32(
                GetSystemMetricsForDpi(SmCxSizeFrame, dpi) + padding,
                GetSystemMetricsForDpi(SmCySizeFrame, dpi) + padding),
            dpi / 96d);
    }

    private static SizeInt32 GetInitialSize(IExperience experience)
    {
        SizeInt32 contentSize = experience switch
        {
            PowderOrbExperience => new SizeInt32(560, 440),
            ZenGardenExperience => new SizeInt32(640, 480),
            _ => new SizeInt32(600, 440),
        };
        return contentSize;
    }

    private AppWindow GetAppWindow()
    {
        IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(handle);
        return AppWindow.GetFromWindowId(windowId);
    }

    private static void ClearInitialBackground(IntPtr windowHandle)
    {
        IntPtr deviceContext = GetDC(windowHandle);
        if (deviceContext == IntPtr.Zero)
            throw new InvalidOperationException("Could not access the compact window background.");

        try
        {
            ClearNativeBackground(windowHandle, deviceContext);
        }
        finally
        {
            ReleaseDC(windowHandle, deviceContext);
        }
    }

    private static void ClearNativeBackground(IntPtr windowHandle, IntPtr deviceContext)
    {
        // The default theme-colored HWND erase remains opaque beneath transparent XAML.
        // A black native surface lets DWM composite the backdrop and artwork with alpha.
        if (!GetClientRect(windowHandle, out WindowRect rect) ||
            FillRect(deviceContext, ref rect, GetStockObject(BlackBrush)) == 0)
        {
            throw new InvalidOperationException("Could not clear the transparent window background.");
        }
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (message == WmSetCursor)
        {
            int hit = unchecked((short)lParam.ToInt64());
            if (hit is >= 10 and <= 17)
                return DefWindowProc(windowHandle, message, wParam, lParam);
        }

        if (!_hasRoundedWindow && message == WmNcPaint)
            return IntPtr.Zero;

        if (!_hasRoundedWindow && message == WmNcActivate)
        {
            // Keep native activation semantics, but never repaint the removed frame.
            return DefWindowProc(windowHandle, message, wParam, new IntPtr(-1));
        }

        if (!_hasRoundedWindow && message == WmNcCalcSize && lParam != IntPtr.Zero)
        {
            // RECT is also the first member of NCCALCSIZE_PARAMS (wParam == TRUE).
            // Own the complete client area; the leftover native resize band paints white.
            WindowRect proposed = Marshal.PtrToStructure<WindowRect>(lParam);
            RectInt32? workArea = null;
            if (IsZoomed(windowHandle))
            {
                var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(MonitorFromRect(ref proposed, 2), ref monitorInfo))
                    workArea = ToBounds(monitorInfo.WorkArea);
                else
                    Debug.WriteLine($"[CompactWindow] Could not read maximized work area: {Marshal.GetLastWin32Error()}");
            }
            RectInt32 bounds = CompactWindowGeometry.GetClientBounds(ToBounds(proposed), workArea);
            var client = new WindowRect
            {
                Left = bounds.X, Top = bounds.Y,
                Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height,
            };
            Marshal.StructureToPtr(client, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcHitTest && GetClientRect(windowHandle, out WindowRect hitTestClient))
        {
            SizeInt32 border = GetResizeBorderSize(windowHandle);
            long coordinates = lParam.ToInt64();
            var cursor = new NativePoint
            {
                X = unchecked((short)coordinates),
                Y = unchecked((short)(coordinates >> 16)),
            };
            if (!ScreenToClient(windowHandle, ref cursor))
                Debug.WriteLine("[CompactWindow] Could not map the resize pointer to client coordinates.");
            else
            {
                int hit = CompactWindowGeometry.GetResizeHit(
                    ToBounds(hitTestClient), new PointInt32(cursor.X, cursor.Y), border, IsZoomed(windowHandle));
                if (hit != 0) return new IntPtr(hit);
            }
        }

        if (message == WmEraseBackground)
        {
            ClearNativeBackground(windowHandle, wParam);
            return new IntPtr(1);
        }

        if (message == WmDwmCompositionChanged)
        {
            ApplyWindowTransparency();
            return IntPtr.Zero;
        }

        IntPtr result = _previousWindowProcedure != IntPtr.Zero
            ? CallWindowProc(
                _previousWindowProcedure,
                windowHandle,
                message,
                wParam,
                lParam)
            : DefWindowProc(windowHandle, message, wParam, lParam);
        if (message == WmDpiChanged)
            ApplyWindowRegion(windowHandle);
        if (message != WmGetMinMaxInfo)
            return result;

        MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        int minimumSize = _isZenGarden
            ? ZenGardenMinimumWindowSize
            : DefaultMinimumWindowSize;
        info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, minimumSize);
        info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, minimumSize);
        Marshal.StructureToPtr(info, lParam, false);
        return result;
    }

    private static RectInt32 ToBounds(WindowRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private void RestoreWindowProcedure()
    {
        if (_previousWindowProcedure == IntPtr.Zero)
            return;

        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(windowHandle, GwlWndProc, _previousWindowProcedure);
        _previousWindowProcedure = IntPtr.Zero;
    }

    internal async Task ShowSessionCompletionDialogAsync()
    {
        XamlRoot? xamlRoot = Root.XamlRoot;
        if (xamlRoot == null)
        {
            ReturnToMain();
            Close();
            return;
        }

        AppWindow appWindow = GetAppWindow();
        PointInt32 position = appWindow.Position;
        SizeInt32 size = appWindow.Size;
        var originalBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
        RectInt32 dialogBounds = GetDialogHostBounds(originalBounds, xamlRoot.RasterizationScale);
        bool resized = dialogBounds.Width != originalBounds.Width ||
                       dialogBounds.Height != originalBounds.Height;

        if (resized)
        {
            appWindow.MoveAndResize(dialogBounds);
            await Task.Yield();
            Root.UpdateLayout();
        }

        try
        {
            await SessionCompletionDialog.ShowAsync(xamlRoot);
        }
        finally
        {
            if (resized)
                appWindow.MoveAndResize(originalBounds);
        }

        ReturnToMain();
        Close();
    }

    private static RectInt32 GetDialogHostBounds(RectInt32 currentBounds, double rasterizationScale)
    {
        RectInt32 workArea = DisplayArea
            .GetFromRect(currentBounds, DisplayAreaFallback.Primary)
            .WorkArea;
        int requiredWidth = (int)Math.Ceiling(MinimumDialogHostWidth * rasterizationScale);
        int requiredHeight = (int)Math.Ceiling(MinimumDialogHostHeight * rasterizationScale);
        int width = Math.Min(Math.Max(currentBounds.Width, requiredWidth), workArea.Width);
        int height = Math.Min(Math.Max(currentBounds.Height, requiredHeight), workArea.Height);
        int left = Math.Clamp(
            currentBounds.X - ((width - currentBounds.Width) / 2),
            workArea.X,
            workArea.X + workArea.Width - width);
        int top = Math.Clamp(
            currentBounds.Y - ((height - currentBounds.Height) / 2),
            workArea.Y,
            workArea.Y + workArea.Height - height);
        return new RectInt32(left, top, width, height);
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ReturnToMain();
        Close();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _owner.ToggleActiveExperiencePause();
        UpdateControls();
    }

    private void TimerTrack_Click(object sender, RoutedEventArgs e)
    {
        DurationComboBox.ShowAt(TimerTrack);
    }

    private void DurationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string tag } selectedItem &&
            int.TryParse(tag, out int minutes))
        {
            foreach (ToggleMenuFlyoutItem item in DurationComboBox.Items)
                item.IsChecked = ReferenceEquals(item, selectedItem);
            DurationText.Text = MainWindow.FormatDurationLabel(tag);
            _owner.SetSessionDurationMinutes(minutes);
            UpdateControls();
        }
    }

    private void Root_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        bool isNearTop = e.GetCurrentPoint(Root).Position.Y <= CompactControlsHeight;
        SetControlsVisible(isNearTop);
    }

    private void Root_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        UpdateControlsVisibilityFromCursor();
    }

    private void GrabHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(GrabHandle).Properties.IsLeftButtonPressed)
            return;

        const uint WmNcLButtonDown = 0x00A1;
        const int HtCaption = 2;
        e.Handled = true;
        ReleaseCapture();
        SendMessage(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            WmNcLButtonDown,
            new IntPtr(HtCaption),
            IntPtr.Zero);
    }

    private void SetControlsVisible(bool visible)
    {
        visible |= DurationComboBox.IsOpen;
        if (_controlsVisible == visible)
            return;

        _controlsVisible = visible;
        double pillOpacity = ControlsPill.Opacity;
        double pillWidth = ControlsPill.Width;
        double pillHeight = ControlsPill.Height;
        double pillOffset = PillTransform.TranslateY;
        double contentOpacity = PillContent.Opacity;
        double countdownOpacity = PillCountdownStroke.Opacity;
        double handleOpacity = GrabHandle.Opacity;
        _controlsTransition?.Stop();

        ControlsPill.Opacity = visible ? 1 : 0;
        ControlsPill.Width = visible ? ExpandedPillWidth : CollapsedPillWidth;
        ControlsPill.Height = visible ? ExpandedPillHeight : CollapsedPillHeight;
        PillTransform.TranslateY = visible ? 0 : CollapsedPillOffset;
        PillContent.Opacity = visible ? 1 : 0;
        PillCountdownStroke.Opacity = visible ? 1 : 0;
        GrabHandle.Opacity = visible ? 0 : 0.72;
        ControlsPill.IsHitTestVisible = visible;
        GrabHandle.IsHitTestVisible = !visible;

        var easing = new CubicEase
        {
            EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseInOut,
        };
        TimeSpan duration = TimeSpan.FromMilliseconds(260);
        _controlsTransition = new Storyboard();
        AddTransition(
            _controlsTransition,
            ControlsPill,
            "Opacity",
            pillOpacity,
            ControlsPill.Opacity,
            duration,
            easing);
        AddTransition(
            _controlsTransition,
            ControlsPill,
            "Width",
            pillWidth,
            ControlsPill.Width,
            duration,
            easing,
            enableDependentAnimation: true);
        AddTransition(
            _controlsTransition,
            ControlsPill,
            "Height",
            pillHeight,
            ControlsPill.Height,
            duration,
            easing,
            enableDependentAnimation: true);
        AddTransition(
            _controlsTransition,
            PillTransform,
            "TranslateY",
            pillOffset,
            PillTransform.TranslateY,
            duration,
            easing);
        AddTransition(
            _controlsTransition,
            PillContent,
            "Opacity",
            contentOpacity,
            PillContent.Opacity,
            visible ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(80),
            easing,
            visible ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero);
        AddCountdownTransition(
            _controlsTransition,
            countdownOpacity,
            visible,
            duration);
        AddTransition(
            _controlsTransition,
            GrabHandle,
            "Opacity",
            handleOpacity,
            GrabHandle.Opacity,
            duration,
            easing);
        _controlsTransition.Begin();
    }

    private static void AddTransition(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double from,
        double to,
        TimeSpan duration,
        EasingFunctionBase easing,
        TimeSpan? beginTime = null,
        bool enableDependentAnimation = false)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            BeginTime = beginTime,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
            EnableDependentAnimation = enableDependentAnimation,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void AddCountdownTransition(
        Storyboard storyboard,
        double from,
        bool visible,
        TimeSpan pillDuration)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
        };

        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = visible ? 0 : from,
        });

        if (visible)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(pillDuration),
                Value = 0,
            });
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(
                    pillDuration + TimeSpan.FromMilliseconds(180)),
                Value = 1,
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut,
                },
            });
        }
        else
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60)),
                Value = 0,
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn,
                },
            });
        }

        Storyboard.SetTarget(animation, PillCountdownStroke);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
    }

    private void SelectDuration(int minutes)
    {
        foreach (ToggleMenuFlyoutItem item in DurationComboBox.Items)
        {
            if (item.Tag is string tag &&
                int.TryParse(tag, out int itemMinutes) &&
                itemMinutes == minutes)
            {
                item.IsChecked = true;
                DurationText.Text = MainWindow.FormatDurationLabel(tag);
                return;
            }
        }
    }

    private void UpdateControls()
    {
        PauseIcon.Glyph = _owner.IsActiveExperiencePaused ? "\uE768" : "\uE769";
        UpdateTimerRing(_owner.SessionProgress);
        AutomationProperties.SetName(TimerTrack, _owner.SessionTimeAutomationName);
        UpdateControlsVisibilityFromCursor();
        UpdatePillCornerRadius();
    }

    private void UpdatePillCornerRadius()
    {
        ControlsPill.CornerRadius = new CornerRadius(ControlsPill.ActualHeight / 2);
    }

    private void UpdateControlsVisibilityFromCursor()
    {
        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!GetCursorPos(out NativePoint point) ||
            !ScreenToClient(windowHandle, ref point))
        {
            return;
        }

        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        bool isInsideWindow =
            point.X >= 0 &&
            point.Y >= 0 &&
            point.X < Root.ActualWidth * scale &&
            point.Y < Root.ActualHeight * scale;
        SetControlsVisible(
            isInsideWindow &&
            point.Y <= CompactControlsHeight * scale);
    }

    private void UpdateTimerRing(double progress)
    {
        if (_owner.SelectedDurationMinutes == 0)
        {
            PillCountdownStroke.Visibility = Visibility.Collapsed;
            return;
        }

        MainWindow.UpdatePillCountdownStroke(PillCountdownStroke, progress);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        RestoreWindowProcedure();
        SaveWindowPlacement(sender);
        if (!_returningToMain && ExperiencePresenter.Content != null)
            ReturnToMain();
    }

    private void ReturnToMain()
    {
        if (_returningToMain)
            return;

        _returningToMain = true;
        SaveWindowPlacement(GetAppWindow());
        _controlsTimer.Stop();
        Root.SizeChanged -= Root_SizeChanged;
        _resizeBorderCue?.Dispose();
        _themeSubscription.Dispose();
        ExperiencePresenter.Content = null;
        SystemBackdrop = null;
        _restore(_experience);
    }

    private void SaveWindowPlacement(AppWindow appWindow)
    {
        WindowPlacementService.Save(appWindow, _experienceId);
    }
}
