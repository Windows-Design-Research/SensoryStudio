using Microsoft.UI.Dispatching;
using System.Diagnostics;
using Windows.Devices.Haptics;
using Windows.Foundation.Metadata;

namespace SensoryStudio.WinUI.Services;

public enum HapticsType : ushort
{
    Stop = 0x1002,
    Click = 0x1003,
    Hover = 0x1008,
    // Optional in the HID haptics page, so these always need a fallback waveform.
    Success = 0x1009,
    Error = 0x100A,
    Collide = 0x1012,
    Align = 0x1013,
    Step = 0x1014,
    Grow = 0x1015,
}

public sealed class HapticService
{
    // ~2 seconds at a 100 ms interval.
    private const int WarmupMaxAttempts = 20;

    private DispatcherQueueTimer? _warmupTimer;
    private int _warmupAttempts;
    private bool _supported;

    public bool Enabled { get; set; } = true;
    public double Intensity { get; set; } = 1;

    public void Initialize(DispatcherQueue dispatcherQueue)
    {
        _supported = CheckAvailability();
        if (!_supported)
            return;

        _warmupTimer = dispatcherQueue.CreateTimer();
        _warmupAttempts = 0;
        _warmupTimer.Interval = TimeSpan.FromMilliseconds(100);
        _warmupTimer.Tick += WarmupTimer_Tick;
        _warmupTimer.Start();
    }

    public void Send(HapticsType waveform)
    {
        Send(waveform, 1);
    }

    /// <summary>
    /// Sends a discrete pulse, scaled by <paramref name="intensityMultiplier"/> on top of the
    /// global slider. Fallback 0 lets Windows choose a device-appropriate waveform from the
    /// same category when the requested waveform is unsupported. Use SendExact to prevent
    /// substitution. Devices without intensity support still receive an unscaled pulse.
    /// </summary>
    public void Send(HapticsType waveform, double intensityMultiplier)
    {
        if (!Enabled || Intensity <= 0)
            return;

        _supported = _supported || CheckAvailability();
        if (!_supported)
            return;

        double intensity = Math.Clamp(
            (0.35 + (0.65 * Math.Clamp(Intensity, 0, 1))) * Math.Clamp(intensityMultiplier, 0, 1),
            0,
            1);
        if (intensity <= 0)
            return;

        try
        {
            InputHapticsManager manager = InputHapticsManager.GetForCurrentThread();
            if (intensity >= 0.999 || !SupportsIntensity(manager))
            {
                manager.TrySendHapticWaveform((ushort)waveform, 0);
                return;
            }

            if (!manager.TrySendHapticWaveform((ushort)waveform, 0, intensity))
                manager.TrySendHapticWaveform((ushort)waveform, 0);
        }
        catch
        {
            _supported = false;
        }
    }

    /// <summary>
    /// Sends a single textured pulse. <paramref name="intensityMultiplier"/> lets callers
    /// shape a continuous texture (for example a faster drag feeling grainier) without
    /// bypassing the global haptics slider.
    /// </summary>
    public void SendTextured(HapticsType waveform, double intensityMultiplier = 1)
    {
        if (!Enabled || Intensity <= 0)
            return;

        _supported = _supported || CheckAvailability();
        if (!_supported)
            return;

        double intensity = Math.Clamp(
            (0.35 + (0.65 * Math.Clamp(Intensity, 0, 1))) * Math.Clamp(intensityMultiplier, 0, 1),
            0,
            1);
        if (intensity <= 0)
            return;

        try
        {
            InputHapticsManager manager = InputHapticsManager.GetForCurrentThread();
            if (!SupportsIntensity(manager))
            {
                manager.TrySendHapticWaveform((ushort)waveform, 0);
                return;
            }

            if (!manager.TrySendHapticWaveform((ushort)waveform, 0, intensity))
                manager.TrySendHapticWaveform((ushort)waveform, 0);
        }
        catch
        {
            _supported = false;
        }
    }

    /// <summary>
    /// Sends a single discrete pulse that can only ever play <paramref name="waveform"/>.
    /// Passing 0 as the fallback would let Windows substitute a default waveform from the
    /// same category, so the waveform is pinned as its own fallback instead. One attempt,
    /// no retry, no substitution.
    /// </summary>
    public void SendExact(HapticsType waveform)
    {
        if (!Enabled || Intensity <= 0)
            return;

        _supported = _supported || CheckAvailability();
        if (!_supported)
            return;

        double intensity = 0.35 + (0.65 * Math.Clamp(Intensity, 0, 1));
        try
        {
            InputHapticsManager manager = InputHapticsManager.GetForCurrentThread();
            if (intensity >= 0.999 || !SupportsIntensity(manager))
            {
                // Same waveform, device-default strength.
                manager.TrySendHapticWaveform((ushort)waveform, (ushort)waveform);
                return;
            }

            manager.TrySendHapticWaveform((ushort)waveform, (ushort)waveform, intensity);
        }
        catch
        {
            _supported = false;
        }
    }

    /// <summary>
    /// Development aid for verifying that haptics follow the device currently driving the
    /// pointer. No controller is pinned or overridden; this only reports what the platform
    /// has routed to.
    /// </summary>
    [Conditional("DEBUG")]
    public void LogRouting(string context, object? pointerDeviceType)
    {
        if (!_supported)
        {
            Debug.WriteLine($"[Haptics] {context}: unsupported (CurrentDeviceType={pointerDeviceType})");
            return;
        }

        try
        {
            InputHapticsManager manager = InputHapticsManager.GetForCurrentThread();
            Debug.WriteLine(
                $"[Haptics] {context}: CurrentDeviceType={pointerDeviceType}, " +
                $"CurrentHapticsControllerDeviceType={manager.CurrentHapticsControllerDeviceType}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Haptics] {context}: routing query failed ({ex.GetType().Name}, HRESULT 0x{ex.HResult:X8})");
        }
    }

    private static bool SupportsIntensity(InputHapticsManager manager)
    {
        try
        {
            return manager.CurrentHapticsController?.IsIntensitySupported != false;
        }
        catch
        {
            return true;
        }
    }

    public void Stop()
    {
        if (!_supported)
            return;

        try
        {
            InputHapticsManager.GetForCurrentThread().TryStopFeedback();
        }
        catch
        {
            _supported = false;
        }
    }

    public void Cleanup()
    {
        _warmupTimer?.Stop();
        _supported = false;
    }

    private void WarmupTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        // Each tick fires a Stop waveform, which cancels any pulse already playing.
        // Give the device a bounded window to come up, then stop regardless - an
        // unbounded retry would keep cancelling real feedback for the app's lifetime.
        _warmupAttempts++;
        try
        {
            InputHapticsManager manager = InputHapticsManager.GetForCurrentThread();
            if (!manager.TrySendHapticWaveform((ushort)HapticsType.Stop, 0) &&
                _warmupAttempts < WarmupMaxAttempts)
            {
                return;
            }

            sender.Stop();
        }
        catch
        {
            sender.Stop();
            _supported = false;
        }
    }

    private static bool CheckAvailability()
    {
        try
        {
            return ApiInformation.IsTypePresent(
                       "Windows.Devices.Haptics.InputHapticsManager") &&
                   InputHapticsManager.IsSupported() &&
                   InputHapticsManager.IsHapticDevicePresent();
        }
        catch
        {
            return false;
        }
    }
}
