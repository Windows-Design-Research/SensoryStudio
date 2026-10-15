#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI\Experiences\CompactExperienceWindow.xaml.cs') -Raw

# Exercise the actual paint/activation branches without creating or activating a window.
$branches = [regex]::Match($source, '(?s)private IntPtr WindowProcedure\([^)]*\)\s*\{(.*?)\s*if \(!_hasRoundedWindow && message == WmNcCalcSize')
if (-not $branches.Success) {
    throw 'Could not locate the native frame paint dispatch before client-area handling.'
}
$constants = [regex]::Matches($source, 'private const uint Wm(?:NcPaint|NcActivate|SetCursor) = 0x[0-9A-Fa-f]+;')
if ($constants.Count -ne 3) {
    throw 'Expected non-client paint, activation, and cursor message constants.'
}

Add-Type -TypeDefinition @"
using System;

public static class CompactFramePaintProbe
{
    $($constants.Value -join "`n")
    public static int Calls;
    public static IntPtr Handle;
    public static uint Message;
    public static IntPtr WParam;
    public static IntPtr LParam;

    private static IntPtr DefWindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        Calls++;
        Handle = handle;
        Message = message;
        WParam = wParam;
        LParam = lParam;
        return new IntPtr(1234);
    }

    public static IntPtr Dispatch(bool _hasRoundedWindow, uint message, IntPtr wParam, IntPtr lParam)
    {
        Calls = 0;
        IntPtr windowHandle = new IntPtr(42);
        $($branches.Groups[1].Value)
        return new IntPtr(999);
    }
}
"@

$passed = 0
foreach ($rounded in @($false, $true)) {
    foreach ($message in @(0x0085, 0x0086, 0x0083, 0x0084, 0x0006, 0x00A1)) {
        foreach ($active in @(0, 1)) {
            $result = [CompactFramePaintProbe]::Dispatch($rounded, $message, [IntPtr]$active, [IntPtr]77)
            $expected = 999
            $expectedCalls = 0
            if (-not $rounded -and $message -eq 0x0085) { $expected = 0 }
            if (-not $rounded -and $message -eq 0x0086) {
                $expected = 1234
                $expectedCalls = 1
                if ([CompactFramePaintProbe]::Handle -ne [IntPtr]42 -or
                    [CompactFramePaintProbe]::Message -ne 0x0086 -or
                    [CompactFramePaintProbe]::WParam -ne [IntPtr]$active -or
                    [CompactFramePaintProbe]::LParam -ne [IntPtr](-1)) {
                    throw 'Activation must preserve the handle/state and disable native repaint with lParam=-1.'
                }
            }
            if ($result -ne [IntPtr]$expected -or [CompactFramePaintProbe]::Calls -ne $expectedCalls) {
                throw "Incorrect frame dispatch: rounded=$rounded, message=$message, active=$active."
            }
            $passed++
        }
    }
}

foreach ($rounded in @($false, $true)) {
    foreach ($hit in @(-1, 0, 1, 2, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18)) {
        $lParam = [IntPtr]((0x0200 -shl 16) -bor ($hit -band 0xffff))
        $result = [CompactFramePaintProbe]::Dispatch($rounded, 0x0020, [IntPtr]82, $lParam)
        $resizing = $hit -ge 10 -and $hit -le 17
        if ($resizing) {
            if ($result -ne [IntPtr]1234 -or [CompactFramePaintProbe]::Calls -ne 1 -or
                [CompactFramePaintProbe]::Message -ne 0x0020 -or
                [CompactFramePaintProbe]::WParam -ne [IntPtr]82 -or
                [CompactFramePaintProbe]::LParam -ne $lParam) {
                throw "Resize hit $hit must use native cursor selection with unchanged message parameters."
            }
        } elseif ($result -ne [IntPtr]999 -or [CompactFramePaintProbe]::Calls -ne 0) {
            throw 'Non-resize cursors must remain owned by the experience/framework.'
        }
        $passed++
    }
}

Write-Output "Passed $passed native frame/cursor dispatch checks (actual source branches; recording stubs; no windows opened)."
