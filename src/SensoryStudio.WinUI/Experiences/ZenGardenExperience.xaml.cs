using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using SensoryStudio.WinUI.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SensoryStudio.WinUI.Experiences;

public sealed partial class ZenGardenExperience : UserControl, IExperience, IPausableExperience, IGardenSettings, IAudioExperience
{
    private const uint SpiSetCursors = 0x0057;
    private const uint OcrNormal = 32512;
    private const bool UseLegacyPlants = false;
    private const bool UseFantasyGrass = true;

    private bool _initialized;
    private LocalWebViewPolicy? _webViewPolicy;
    private double _volume;
    private readonly DispatcherQueueTimer _cursorTimer;
    private IntPtr _windowHandle;
    private NativeRect _browserClientBounds;
    private IntPtr _transparentCursor;
    private bool _cursorHidden;
    private bool _isPlantEditorActive;
    private readonly List<FrameworkElement> _cursorExclusionElements = [];
    private FrameworkElement? _rockDropTarget;
    private Windows.Foundation.Rect? _lastRockDropZone;
    public int RockInventory { get; private set; } = 3;
    public event EventHandler<int>? RockInventoryChanged;
    public event EventHandler<bool>? RockDropProximityChanged;

    public ZenGardenExperience()
    {
        InitializeComponent();
        _cursorTimer = DispatcherQueue.CreateTimer();
        _cursorTimer.Interval = TimeSpan.FromMilliseconds(16);
        _cursorTimer.Tick += CursorTimer_Tick;
        Loaded += ZenGardenExperience_Loaded;
        Unloaded += ZenGardenExperience_Unloaded;
        Browser.LayoutUpdated += Browser_LayoutUpdated;
    }

    public string Title => "Zen Garden";
    public UserControl View => this;

    public async void Activate()
    {
        if (_initialized)
            return;
        _initialized = true;
        SoundSettingsService.Register(this);
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;

        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.IsMuted = _volume <= 0;
            _webViewPolicy?.Dispose();
            _webViewPolicy = new LocalWebViewPolicy(Browser.CoreWebView2, garden: true);
            Browser.CoreWebView2.WebMessageReceived -= Browser_WebMessageReceived;
            Browser.CoreWebView2.WebMessageReceived += Browser_WebMessageReceived;
            Browser.NavigationCompleted -= Browser_NavigationCompleted;
            Browser.NavigationCompleted += Browser_NavigationCompleted;
            string gardenAssetsPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "ZenGarden");
            const string gardenBaseUrl = LocalWebViewPolicy.GardenOrigin;
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "zen-garden.local",
                gardenAssetsPath,
                CoreWebView2HostResourceAccessKind.Deny);
            string rakeCursorVariables = string.Concat(
                new[] { 1, 3, 5, 7, 9 }.Select(tines =>
                {
                    return $"--rake-cursor-image-{tines}:" +
                        $"url(\"{gardenBaseUrl}/{tines}.png\");";
                }));
            string[] grassImages = Enumerable.Range(1, 3)
                .Select(index => $"{gardenBaseUrl}/Grass{index}.webp")
                .ToArray();
            string[] grassStrandImages = Enumerable.Range(1, 4)
                .Select(index => $"{gardenBaseUrl}/GrassStrand{index}.webp")
                .ToArray();
            string[] flowerImages = Enumerable.Range(1, 3)
                .Select(index => $"{gardenBaseUrl}/Flower{index}.webp")
                .ToArray();
            string[] tinyFlowerImages = Enumerable.Range(1, 8)
                .Select(index => $"{gardenBaseUrl}/TinyFlower{index}.png")
                .ToArray();
            string[] mossImages = Enumerable.Range(1, 3)
                .Select(index => $"{gardenBaseUrl}/MossBed{index}.webp")
                .ToArray();
            string gardenAssets = JsonSerializer.Serialize(new
            {
                legacyPlants = new
                {
                    grass = grassImages,
                    grassStrands = grassStrandImages,
                    flowers = flowerImages,
                    tinyFlowers = tinyFlowerImages,
                    moss = mossImages,
                },
            });
            string gardenFeatures = JsonSerializer.Serialize(new
            {
                legacyPlants = UseLegacyPlants,
                fantasyGrass = UseFantasyGrass,
                fantasyGrassHeightScale = 0.68,
            });
            string hapticsEnabled = JsonSerializer.Serialize(
                AppSettingsService.Current.HapticsEnabled);
            string savedPlantLayout = GetSavedPlantLayoutScriptValue();
            string bootstrapScript =
                $"if(window.top===window&&location.href==='{LocalWebViewPolicy.GardenDocument}'){{" +
                $"window.__gardenAssets={gardenAssets};" +
                $"window.__gardenFeatures={gardenFeatures};" +
                $"window.__hapticsEnabled={hapticsEnabled};" +
                $"window.__soundVolume={JsonSerializer.Serialize(_volume)};" +
                $"window.__savedPlantLayout=JSON.parse({JsonSerializer.Serialize(savedPlantLayout)});" +
                "document.addEventListener('DOMContentLoaded',()=>{" +
                "const style=document.createElement('style');" +
                $"style.textContent=':root{{{rakeCursorVariables}" +
                "--rake-cursor-image:var(--rake-cursor-image-5);}}';" +
                "document.head.appendChild(style);});}";
            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                bootstrapScript);
            Browser.CoreWebView2.Navigate(LocalWebViewPolicy.GardenDocument);
        }
        catch (Exception exception)
        {
            SoundSettingsService.Unregister(this);
            _initialized = false;
            ErrorText.Text = $"Zen Garden could not start.\n{exception.Message}";
            ErrorPanel.Visibility = Visibility.Visible;
        }
    }

    public void Deactivate()
    {
        SoundSettingsService.Unregister(this);
        _isPlantEditorActive = false;
        ExperienceFeedback.Haptics.Stop();
        Browser.NavigationCompleted -= Browser_NavigationCompleted;
        if (Browser.CoreWebView2 is { } core)
            core.WebMessageReceived -= Browser_WebMessageReceived;
        _webViewPolicy?.Dispose();
        _webViewPolicy = null;
        Browser.Close();
    }

    public bool IsPaused { get; private set; }

    /// <summary>
    /// Freezes the simulation in place instead of tearing down the WebView, so the
    /// garden - and its haptics - survive a session-timer pause.
    /// </summary>
    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
            ExperienceFeedback.Haptics.Stop();
        PostToGarden(new { type = "pause", value = IsPaused });
    }

    public void SetParameter(string key, double value)
    {
        PostToGarden(new { type = "set", key, value });
    }

    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        if (Browser.CoreWebView2 is { } core)
        {
            core.IsMuted = _volume <= 0;
            PostToGarden(new { type = "volume", value = _volume });
        }
    }

    public void SetHapticsEnabled(bool enabled)
    {
        if (!enabled)
            ExperienceFeedback.Haptics.Stop();
        PostToGarden(new { type = "haptics", enabled });
    }

    public void RunAction(string action)
    {
        PostToGarden(new { type = "action", name = action });
    }

    public void SetHostWindow(
        Window window,
        params FrameworkElement[] cursorExclusionElements)
    {
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _cursorExclusionElements.Clear();
        _cursorExclusionElements.AddRange(cursorExclusionElements);
        UpdateBrowserClientBounds();
    }

    public void SetRockDropTarget(FrameworkElement target)
    {
        _rockDropTarget = target;
        if (!_initialized ||
            target.XamlRoot != XamlRoot ||
            target.ActualWidth <= 0 ||
            target.ActualHeight <= 0)
        {
            return;
        }

        GeneralTransform transform = target.TransformToVisual(Browser);
        Windows.Foundation.Point origin = transform.TransformPoint(new Windows.Foundation.Point());
        Windows.Foundation.Rect zone = new(
            origin.X,
            origin.Y,
            target.ActualWidth,
            target.ActualHeight);
        if (_lastRockDropZone is Windows.Foundation.Rect lastZone &&
            lastZone.Equals(zone))
            return;

        _lastRockDropZone = zone;
        PostToGarden(new
        {
            type = "rockDropZone",
            x = zone.X,
            y = zone.Y,
            width = zone.Width,
            height = zone.Height,
        });
    }

    private void Browser_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        _lastRockDropZone = null;
        if (args.IsSuccess)
        {
            SetVolume(_volume);
            LoadingPanel.Visibility = Visibility.Collapsed;
            if (_rockDropTarget is not null)
                SetRockDropTarget(_rockDropTarget);
            return;
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = $"Zen Garden could not load.\n{args.WebErrorStatus}";
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void ZenGardenExperience_Loaded(object sender, RoutedEventArgs args)
    {
        EnsureWindowHandle();
        UpdateBrowserClientBounds();
        AppDomain.CurrentDomain.ProcessExit -= CurrentDomain_ProcessExit;
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        _cursorTimer.Start();
    }

    private void ZenGardenExperience_Unloaded(object sender, RoutedEventArgs args)
    {
        _isPlantEditorActive = false;
        _cursorTimer.Stop();
        RestoreSystemCursor();
        ReleaseCursor();
        AppDomain.CurrentDomain.ProcessExit -= CurrentDomain_ProcessExit;
        _windowHandle = IntPtr.Zero;
    }

    private void CurrentDomain_ProcessExit(object? sender, EventArgs args)
    {
        RestoreSystemCursor();
        ReleaseCursor();
    }

    private void Browser_LayoutUpdated(object? sender, object args)
    {
        EnsureWindowHandle();
        UpdateBrowserClientBounds();
    }

    private void EnsureWindowHandle()
    {
        if (_windowHandle != IntPtr.Zero || XamlRoot?.ContentIslandEnvironment is null)
            return;

        _windowHandle = Win32Interop.GetWindowFromWindowId(
            XamlRoot.ContentIslandEnvironment.AppWindowId);
    }

    private void CursorTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isPlantEditorActive)
        {
            RestoreSystemCursor();
            return;
        }

        if (_windowHandle == IntPtr.Zero ||
            GetForegroundWindow() != _windowHandle ||
            !GetCursorPos(out NativePoint screenPosition))
        {
            RestoreSystemCursor();
            return;
        }

        NativePoint clientPosition = screenPosition;
        bool isOverBrowser =
            ScreenToClient(_windowHandle, ref clientPosition) &&
            _browserClientBounds.Contains(clientPosition);

        if (isOverBrowser && !IsOverInteractiveUi(clientPosition))
            ReplaceSystemCursor();
        else
            RestoreSystemCursor();
    }

    private bool IsOverInteractiveUi(NativePoint clientPosition)
    {
        if (_cursorExclusionElements.Any(element =>
            IsPointOverElement(element, clientPosition)))
        {
            return true;
        }

        return XamlRoot is not null &&
            VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(
                popup => popup.Child is FrameworkElement element &&
                    IsPointOverElement(element, clientPosition));
    }

    private bool IsPointOverElement(
        FrameworkElement element,
        NativePoint clientPosition)
    {
        if (XamlRoot is null ||
            element.XamlRoot != XamlRoot ||
            element.Visibility != Visibility.Visible ||
            !element.IsHitTestVisible ||
            element.Opacity <= 0 ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0)
        {
            return false;
        }

        GeneralTransform transform = element.TransformToVisual(null);
        Windows.Foundation.Point origin =
            transform.TransformPoint(new Windows.Foundation.Point());
        double scale = XamlRoot.RasterizationScale;
        var bounds = new NativeRect
        {
            Left = (int)Math.Floor(origin.X * scale),
            Top = (int)Math.Floor(origin.Y * scale),
            Right = (int)Math.Ceiling((origin.X + element.ActualWidth) * scale),
            Bottom = (int)Math.Ceiling((origin.Y + element.ActualHeight) * scale),
        };
        return bounds.Contains(clientPosition);
    }

    private void ReplaceSystemCursor()
    {
        if (_cursorHidden)
            return;

        EnsureTransparentCursor();
        IntPtr cursorCopy = CopyIcon(_transparentCursor);
        if (cursorCopy != IntPtr.Zero && SetSystemCursor(cursorCopy, OcrNormal))
            _cursorHidden = true;
    }

    private void EnsureTransparentCursor()
    {
        if (_transparentCursor != IntPtr.Zero)
            return;

        const int cursorSize = 32;
        byte[] andMask = new byte[cursorSize * cursorSize / 8];
        Array.Fill(andMask, byte.MaxValue);
        byte[] xorMask = new byte[andMask.Length];
        _transparentCursor = CreateCursor(
            IntPtr.Zero,
            0,
            0,
            cursorSize,
            cursorSize,
            andMask,
            xorMask);
    }

    private void RestoreSystemCursor()
    {
        if (!_cursorHidden)
            return;

        SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 0);
        _cursorHidden = false;
    }

    private void ReleaseCursor()
    {
        if (_transparentCursor == IntPtr.Zero)
            return;

        DestroyCursor(_transparentCursor);
        _transparentCursor = IntPtr.Zero;
    }

    private void UpdateBrowserClientBounds()
    {
        if (_windowHandle == IntPtr.Zero ||
            XamlRoot is null ||
            Browser.ActualWidth <= 0 ||
            Browser.ActualHeight <= 0)
        {
            _browserClientBounds = default;
            return;
        }

        GeneralTransform transform = Browser.TransformToVisual(null);
        Windows.Foundation.Point origin =
            transform.TransformPoint(new Windows.Foundation.Point());
        double scale = XamlRoot.RasterizationScale;
        _browserClientBounds = new NativeRect
        {
            Left = (int)Math.Round(origin.X * scale),
            Top = (int)Math.Round(origin.Y * scale),
            Right = (int)Math.Round((origin.X + Browser.ActualWidth) * scale),
            Bottom = (int)Math.Round((origin.Y + Browser.ActualHeight) * scale),
        };
    }

    private void PostToGarden(object message)
    {
        if (Browser.CoreWebView2 is { } core && LocalWebViewPolicy.IsGardenDocument(core.Source))
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void Browser_WebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!GardenMessage.TryParse(args.Source, args.WebMessageAsJson, out GardenMessage? message, out string error))
        {
            Trace.TraceWarning($"Rejected garden message: {error}");
            return;
        }
        if (message.Type is "ready" or "diagnostic")
        {
            Trace.WriteLine("Received garden readiness/diagnostic message.");
            return;
        }
        if (message.Type == "plant-layout")
        {
            AppSettingsService.Current.ZenGardenPlantLayout = message.Layout;
            AppSettingsService.Save();
            return;
        }

        if (message.Type == "plant-editor")
        {
            _isPlantEditorActive = message.Active;
            if (_isPlantEditorActive)
            {
                ExperienceFeedback.Haptics.Stop();
                RestoreSystemCursor();
            }
            return;
        }

        if (message.Type == "rock-inventory")
        {
            RockInventory = message.Count;
            RockInventoryChanged?.Invoke(this, RockInventory);
            return;
        }

        if (message.Type == "rock-drop-proximity")
        {
            RockDropProximityChanged?.Invoke(this, message.Active);
            return;
        }

        double intensity = message.Intensity;

        // The garden page owns cadence and collision cooldowns; this only maps semantic
        // events to waveforms. Step is deliberately unused - it reads muddy on hardware.
        switch (message.Interaction)
        {
            case "rake":
                ExperienceFeedback.Haptics.Send(HapticsType.Hover, intensity);
                break;
            case "collision":
                ExperienceFeedback.Haptics.Send(HapticsType.Collide, intensity);
                break;
            case "rock-aligned":
                ExperienceFeedback.Haptics.Send(HapticsType.Align);
                break;
            case "rock-picked-up":
                ExperienceFeedback.Haptics.Send(HapticsType.Hover);
                break;
            case "rock-drag-start":
                ExperienceFeedback.Haptics.Send(HapticsType.Grow);
                break;
            case "grass":
                ExperienceFeedback.Haptics.SendTextured(
                    HapticsType.Hover,
                    intensity);
                break;
            case "stack-failed":
                ExperienceFeedback.Haptics.Send(HapticsType.Error, intensity);
                break;
            case "stack-complete":
                ExperienceFeedback.Haptics.Send(HapticsType.Success, intensity);
                break;
        }
    }

    private static string GetSavedPlantLayoutScriptValue()
    {
        string? savedLayout = AppSettingsService.Current.ZenGardenPlantLayout;
        if (string.IsNullOrWhiteSpace(savedLayout))
            return "null";

        try
        {
            using JsonDocument document = JsonDocument.Parse(savedLayout);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.GetRawText()
                : "null";
        }
        catch (JsonException)
        {
            return "null";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(
        IntPtr instance,
        int hotSpotX,
        int hotSpotY,
        int width,
        int height,
        byte[] andMask,
        byte[] xorMask);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemCursor(IntPtr cursor, uint cursorId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        IntPtr value,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly bool Contains(NativePoint point) =>
            point.X >= Left &&
            point.X < Right &&
            point.Y >= Top &&
            point.Y < Bottom;
    }

}
