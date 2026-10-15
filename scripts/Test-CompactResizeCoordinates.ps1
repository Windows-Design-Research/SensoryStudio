#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI\Experiences\CompactExperienceWindow.xaml.cs') -Raw
$geometry = Get-Content (Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI\Services\CompactWindowGeometry.cs') -Raw
$hitTest = [regex]::Match($source, '(?s)if \(message == WmNcHitTest && GetClientRect\(.*?(?=\s*if \(message == WmEraseBackground)')
if (-not $hitTest.Success) { throw 'Could not locate the client-coordinate native hit-test branch.' }
$geometry = $geometry.Replace('using Windows.Graphics;', '').Replace(
    'namespace SensoryStudio.WinUI.Services;', 'namespace SensoryStudio.WinUI.Services {') + "`n}"

# Use actual hit-test dispatch and geometry with recording Win32 coordinate stubs.
Add-Type -TypeDefinition @"
using System;
using System.Diagnostics;
using Windows.Graphics;

namespace Windows.Graphics
{
    public struct PointInt32
    {
        public int X, Y;
        public PointInt32(int x, int y) { X = x; Y = y; }
    }
    public struct SizeInt32
    {
        public int Width, Height;
        public SizeInt32(int width, int height) { Width = width; Height = height; }
    }
    public struct RectInt32
    {
        public int X, Y, Width, Height;
        public RectInt32(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
    }
}
$geometry
namespace SensoryStudio.WinUI.Services
{
    public static class ResizeCoordinateProbe
    {
        private const uint WmNcHitTest = 0x0084;
        private struct WindowRect { public int Left, Top, Right, Bottom; }
        private struct NativePoint { public int X, Y; }
        public static int ClientX, ClientY, Border;
        public static bool Maximized;
        public static int Conversions;
        private static bool GetClientRect(IntPtr handle, out WindowRect rect)
        {
            rect = new WindowRect { Left = 0, Top = 0, Right = 720, Bottom = 380 };
            return true;
        }
        private static bool ScreenToClient(IntPtr handle, ref NativePoint cursor)
        {
            Conversions++;
            cursor.X -= ClientX;
            cursor.Y -= ClientY;
            return true;
        }
        private static SizeInt32 GetResizeBorderSize(IntPtr handle) => new SizeInt32(Border, Border);
        private static bool IsZoomed(IntPtr handle) => Maximized;
        private static RectInt32 ToBounds(WindowRect rect) =>
            new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

        public static IntPtr Hit(int screenX, int screenY)
        {
            Conversions = 0;
            IntPtr windowHandle = new IntPtr(42);
            uint message = WmNcHitTest;
            IntPtr lParam = new IntPtr((screenX & 0xffff) | ((screenY & 0xffff) << 16));
            $($hitTest.Value)
            return new IntPtr(999);
        }
    }
}
"@

$passed = 0
foreach ($origin in @(@(0, 0), @(108, 128), @(-1892, -892), @(1940, 48))) {
    [SensoryStudio.WinUI.Services.ResizeCoordinateProbe]::ClientX = $origin[0]
    [SensoryStudio.WinUI.Services.ResizeCoordinateProbe]::ClientY = $origin[1]
    foreach ($border in @(12, 15, 18, 24, 36)) {
        [SensoryStudio.WinUI.Services.ResizeCoordinateProbe]::Border = $border
        foreach ($case in @(
            @(0, 190, 10), @(719, 190, 11), @(360, 0, 12), @(360, 379, 15),
            @(0, 0, 13), @(719, 0, 14), @(0, 379, 16), @(719, 379, 17),
            @(360, 190, 999), @($border, $border, 999), @(-1, 190, 999), @(720, 190, 999)
        )) {
            foreach ($maximized in @($false, $true)) {
                [SensoryStudio.WinUI.Services.ResizeCoordinateProbe]::Maximized = $maximized
                $expected = if ($maximized) { 999 } else { $case[2] }
                $actual = [SensoryStudio.WinUI.Services.ResizeCoordinateProbe]::Hit(
                    $origin[0] + $case[0], $origin[1] + $case[1])
                if ($actual -ne [IntPtr]$expected -or
                    [SensoryStudio.WinUI.Services.ResizeCoordinateProbe]::Conversions -ne 1) {
                    throw "Incorrect client-space resize hit: origin=$origin, border=$border, point=$case, maximized=$maximized."
                }
                $passed++
            }
        }
    }
}

Write-Output "Passed $passed client-coordinate resize checks (actual dispatch/geometry; recording Win32 stubs; no windows opened)."
