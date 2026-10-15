#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI\Experiences\CompactExperienceWindow.xaml.cs') -Raw
$method = [regex]::Match($source, '(?s)private void UpdateResizeInputRegions\(.*?(?=\s*private static SizeInt32 GetResizeBorderSize)')
$presenter = [regex]::Match($source, '(?s)if \(appWindow.Presenter is OverlappedPresenter presenter\)\s*\{.*?\}')
if (-not $method.Success -or -not $presenter.Success) {
    throw 'Could not locate resize input registration and presenter setup.'
}

# Compile the production registration/setup code against recording WinUI stubs.
# Region geometry itself is exercised against the built app in Test-CompactWindowGeometry.
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;

namespace CompactResizeInputTest
{
    public struct SizeInt32 { }
    public struct RectInt32 { public int Marker; }
    public enum NonClientRegionKind { LeftBorder, TopBorder, RightBorder, BottomBorder }
    public sealed class InputNonClientPointerSource
    {
        public readonly List<string> Calls = new List<string>();
        public void SetRegionRects(NonClientRegionKind kind, RectInt32[] rects)
        {
            if (rects.Length != 1) throw new Exception("Expected one rectangle per border.");
            Calls.Add("Set:" + kind + ":" + rects[0].Marker);
        }
        public void ClearRegionRects(NonClientRegionKind kind) { Calls.Add("Clear:" + kind); }
    }
    public static class CompactWindowGeometry
    {
        public static RectInt32[] GetResizeRegions(SizeInt32 size, SizeInt32 border, bool maximized)
        {
            if (maximized) return Array.Empty<RectInt32>();
            return new[] {
                new RectInt32 { Marker = 1 }, new RectInt32 { Marker = 2 },
                new RectInt32 { Marker = 3 }, new RectInt32 { Marker = 4 }
            };
        }
    }
    public sealed class OverlappedPresenter
    {
        public bool IsAlwaysOnTop, IsResizable, IsMaximizable, HasBorder, HasTitleBar;
        public void SetBorderAndTitleBar(bool border, bool title) { HasBorder = border; HasTitleBar = title; }
    }
    public sealed class AppWindow { public object Presenter = new OverlappedPresenter(); }
    public sealed class Probe
    {
        private readonly InputNonClientPointerSource _resizeInput;
        public Probe(bool initialized) { _resizeInput = initialized ? new InputNonClientPointerSource() : null; }
        $($method.Value)
        public string[] Update(bool maximized)
        {
            if (_resizeInput != null) _resizeInput.Calls.Clear();
            UpdateResizeInputRegions(new SizeInt32(), new SizeInt32(), maximized);
            return _resizeInput == null ? Array.Empty<string>() : _resizeInput.Calls.ToArray();
        }
        public static OverlappedPresenter Configure(bool _hasRoundedWindow)
        {
            var appWindow = new AppWindow();
            $($presenter.Value)
            return (OverlappedPresenter)appWindow.Presenter;
        }
    }
}
"@

$passed = 0
$probe = [CompactResizeInputTest.Probe]::new($true)
foreach ($maximized in @($false, $true, $false, $false)) {
    $calls = $probe.Update($maximized)
    $expected = if ($maximized) {
        @('Clear:LeftBorder', 'Clear:TopBorder', 'Clear:RightBorder', 'Clear:BottomBorder')
    } else {
        @('Set:LeftBorder:1', 'Set:TopBorder:2', 'Set:RightBorder:3', 'Set:BottomBorder:4')
    }
    if (($calls -join ',') -ne ($expected -join ',')) {
        throw "Incorrect WinUI resize input registration: $($calls -join ',')."
    }
    $passed++
}
if ([CompactResizeInputTest.Probe]::new($false).Update($false).Length -ne 0) {
    throw 'Initial native layout must wait until the input source is available.'
}
$passed++
foreach ($rounded in @($false, $true)) {
    $presenter = [CompactResizeInputTest.Probe]::Configure($rounded)
    if (-not $presenter.IsResizable -or -not $presenter.IsMaximizable -or
        -not $presenter.IsAlwaysOnTop -or $presenter.HasTitleBar -or
        $presenter.HasBorder -ne (-not $rounded)) {
        throw 'Presenter must retain resize capability without restoring the title bar or changing rounded windows.'
    }
    $passed++
}

foreach ($pattern in @(
    '_resizeInput = InputNonClientPointerSource.GetForWindowId(appWindow.Id)',
    'new SizeInt32(client.Right - client.Left, client.Bottom - client.Top)',
    'Root.SizeChanged += Root_SizeChanged',
    'Root.SizeChanged -= Root_SizeChanged',
    'RefreshResizeInputRegions(windowHandle)',
    'if (message == WmDpiChanged)'
)) {
    if (-not $source.Contains($pattern)) { throw "Missing resize lifecycle wiring: $pattern" }
    $passed++
}

Write-Output "Passed $passed resize input registration checks (actual source; recording WinUI stubs; no windows opened)."
