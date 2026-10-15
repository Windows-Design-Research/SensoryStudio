#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI\Services\HapticService.cs') -Raw
# Compile the real service against recording SDK stubs; no physical feedback is sent.
$source = $source.Replace(
    'namespace SensoryStudio.WinUI.Services;',
    'namespace SensoryStudio.WinUI.Services {'
) + "`n}"

$stubsAndTests = @'
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public DispatcherQueueTimer CreateTimer() => new();
    }
    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public event Action<DispatcherQueueTimer, object> Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
    }
}
namespace Windows.Foundation.Metadata
{
    public static class ApiInformation
    {
        public static bool IsTypePresent(string name) => true;
    }
}
namespace Windows.Devices.Haptics
{
    public sealed class RecordingController
    {
        public bool IsIntensitySupported { get; set; } = true;
        public string Id => "test";
    }
    public sealed class InputHapticsManager
    {
        public static InputHapticsManager Instance { get; } = new();
        public readonly System.Collections.Generic.List<(ushort Waveform, ushort Fallback, double? Intensity)> Calls = new();
        public readonly System.Collections.Generic.Queue<bool> Results = new();
        public RecordingController CurrentHapticsController { get; } = new();
        public string CurrentHapticsControllerDeviceType => "Pen";
        public static InputHapticsManager GetForCurrentThread() => Instance;
        public static bool IsSupported() => true;
        public static bool IsHapticDevicePresent() => true;
        public bool TryStopFeedback() => true;
        public bool TrySendHapticWaveform(ushort waveform, ushort fallback)
            => Record(waveform, fallback, null);
        public bool TrySendHapticWaveform(ushort waveform, ushort fallback, double intensity)
            => Record(waveform, fallback, intensity);
        private bool Record(ushort waveform, ushort fallback, double? intensity)
        {
            Calls.Add((waveform, fallback, intensity));
            return Results.Count == 0 || Results.Dequeue();
        }
    }
}
namespace SensoryStudio.HapticTests
{
    using SensoryStudio.WinUI.Services;
    using Windows.Devices.Haptics;

    public static class FallbackTests
    {
        private static int _passed;

        private static void Check(
            string name, HapticsType waveform, Action<HapticService> send,
            bool exact, bool supportsIntensity, bool rejectFirst,
            params double?[] expectedIntensities)
        {
            var manager = InputHapticsManager.Instance;
            manager.Calls.Clear();
            manager.Results.Clear();
            manager.CurrentHapticsController.IsIntensitySupported = supportsIntensity;
            if (rejectFirst)
                manager.Results.Enqueue(false);
            send(new HapticService());
            if (manager.Calls.Count != expectedIntensities.Length)
                throw new Exception($"{name}: unexpected send count {manager.Calls.Count}.");
            for (int i = 0; i < manager.Calls.Count; i++)
            {
                var call = manager.Calls[i];
                ushort fallback = exact ? (ushort)waveform : (ushort)0;
                if (call.Waveform != (ushort)waveform || call.Fallback != fallback)
                    throw new Exception($"{name}: wrong requested or fallback waveform.");
                double? expected = expectedIntensities[i];
                if (call.Intensity.HasValue != expected.HasValue ||
                    (expected.HasValue && Math.Abs(call.Intensity!.Value - expected.Value) > 1e-9))
                    throw new Exception($"{name}: incorrect intensity {call.Intensity}.");
            }
            _passed++;
        }

        public static int Run()
        {
            _passed = 0;
            foreach (HapticsType waveform in Enum.GetValues<HapticsType>())
            {
                Check("Send default", waveform, s => s.Send(waveform),
                    false, true, false, new double?[] { null });
                Check("Send scaled", waveform, s => { s.Intensity = 0.5; s.Send(waveform, 0.4); },
                    false, true, false, 0.27);
                Check("Send without intensity support", waveform, s => s.Send(waveform, 0.4),
                    false, false, false, new double?[] { null });
                Check("Send rejected intensity retry", waveform, s => s.Send(waveform, 0.4),
                    false, true, true, 0.4, null);
                Check("Textured default", waveform, s => s.SendTextured(waveform, 0.4),
                    false, true, false, 0.4);
                Check("Textured retry", waveform, s => s.SendTextured(waveform, 0.4),
                    false, true, true, 0.4, null);
                Check("Exact default", waveform, s => s.SendExact(waveform),
                    true, true, false, new double?[] { null });
                Check("Exact scaled", waveform, s => { s.Intensity = 0.5; s.SendExact(waveform); },
                    true, true, false, 0.675);
                Check("Exact failure does not retry", waveform, s => { s.Intensity = 0.5; s.SendExact(waveform); },
                    true, true, true, 0.675);
                Check("Exact without intensity support", waveform, s => { s.Intensity = 0.5; s.SendExact(waveform); },
                    true, false, false, new double?[] { null });
                Check("Disabled", waveform, s => { s.Enabled = false; s.Send(waveform); },
                    false, true, false);
                Check("Muted", waveform, s => { s.Intensity = 0; s.Send(waveform); },
                    false, true, false);
                Check("Zero multiplier", waveform, s => s.Send(waveform, 0),
                    false, true, false);
            }
            return _passed;
        }
    }
}
'@

Add-Type -TypeDefinition ("#nullable enable`nusing System;`n" + $source + "`n" + $stubsAndTests)
$passed = [SensoryStudio.HapticTests.FallbackTests]::Run()
Write-Output "Passed $passed haptic fallback checks (no hardware used)."
