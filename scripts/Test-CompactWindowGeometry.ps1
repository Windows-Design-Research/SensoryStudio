#Requires -Version 7.4

param(
    [string]$BuildOutput = (Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0')
)

$ErrorActionPreference = 'Stop'
$BuildOutput = (Resolve-Path $BuildOutput).Path
[void][Reflection.Assembly]::LoadFrom((Join-Path $BuildOutput 'WinRT.Runtime.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $BuildOutput 'Microsoft.Windows.SDK.NET.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $BuildOutput 'SensoryStudio.WinUI.dll'))
$geometry = $assembly.GetType('SensoryStudio.WinUI.Services.CompactWindowGeometry', $true)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$clientBounds = $geometry.GetMethod('GetClientBounds', $flags)
$resizeHit = $geometry.GetMethod('GetResizeHit', $flags)
$resizeRegions = $geometry.GetMethod('GetResizeRegions', $flags)
$resizeBorderSize = $geometry.GetMethod('GetResizeBorderSize', $flags)
$passed = 0

foreach ($scale in @(1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0)) {
    foreach ($native in @(@(8, 8), @(24, 16))) {
        $border = [Windows.Graphics.SizeInt32]::new($native[0], $native[1])
        $actual = $resizeBorderSize.Invoke($null, @($border, $scale))
        $minimum = [Math]::Ceiling(12 * $scale)
        if ($actual.Width -ne [Math]::Max($native[0], $minimum) -or
            $actual.Height -ne [Math]::Max($native[1], $minimum)) {
            throw 'Resize targets must be at least 12 DIPs without shrinking larger native borders.'
        }
        $passed++
    }
}

function Rect([int[]]$Values) {
    return [Windows.Graphics.RectInt32]::new($Values[0], $Values[1], $Values[2], $Values[3])
}

function Assert-ClientBounds {
    param([string]$Name, [int[]]$Window, [object]$WorkArea, [int[]]$Expected)
    $work = if ($null -eq $WorkArea) { $null } else { Rect $WorkArea }
    $actual = $clientBounds.Invoke($null, @((Rect $Window), $work))
    $values = @($actual.X, $actual.Y, $actual.Width, $actual.Height)
    if (($values -join ',') -ne ($Expected -join ',')) {
        throw "${Name}: expected $($Expected -join ','), got $($values -join ',')"
    }
    $script:passed++
}

Assert-ClientBounds 'Restored window has no native top strip' @(100, 120, 720, 380) $null @(100, 120, 720, 380)
Assert-ClientBounds 'Negative-origin window has no frame inset' @(-1500, -800, 900, 600) $null @(-1500, -800, 900, 600)
Assert-ClientBounds 'Maximized window excludes offscreen resize margins' @(-8, -8, 1936, 1056) @(0, 0, 1920, 1040) @(0, 0, 1920, 1040)
Assert-ClientBounds 'Taskbar at top' @(-8, -8, 1936, 1096) @(0, 40, 1920, 1040) @(0, 40, 1920, 1040)
Assert-ClientBounds 'Taskbar on left monitor' @(-1936, -16, 1952, 1072) @(-1880, 0, 1880, 1040) @(-1880, 0, 1880, 1040)
Assert-ClientBounds 'Monitor above primary' @(-8, -1208, 1936, 1216) @(0, -1200, 1920, 1160) @(0, -1200, 1920, 1160)
Assert-ClientBounds 'Client stays within proposed window' @(20, 30, 500, 400) @(0, 0, 1920, 1040) @(20, 30, 500, 400)

$window = Rect @(-1500, 100, 720, 380)
foreach ($borderSize in @(8, 12, 16, 24)) {
    $border = [Windows.Graphics.SizeInt32]::new($borderSize, $borderSize)
    $size = [Windows.Graphics.SizeInt32]::new($window.Width, $window.Height)
    $regions = $resizeRegions.Invoke($null, @($size, $border, $false))
    if ($regions.Length -ne 4) { throw 'Every restored window needs four native input borders.' }
    $passed++
    foreach ($case in @(
        @(0, 0, 13), @(360, 0, 12), @(719, 0, 14),
        @(0, 190, 10), @(719, 190, 11),
        @(0, 379, 16), @(360, 379, 15), @(719, 379, 17),
        @($borderSize, $borderSize, 0),
        @(360, 190, 0), @(-1, 190, 0), @(720, 190, 0)
    )) {
        $point = [Windows.Graphics.PointInt32]::new($window.X + $case[0], $window.Y + $case[1])
        $actual = $resizeHit.Invoke($null, @($window, $point, $border, $false))
        if ($actual -ne $case[2]) {
            throw "Resize hit at $($case[0]),$($case[1]) (border $borderSize): expected $($case[2]), got $actual"
        }
        $passed++
        $mask = 0
        for ($i = 0; $i -lt 4; $i++) {
            $r = $regions[$i]
            if ($case[0] -ge $r.X -and $case[0] -lt ($r.X + $r.Width) -and
                $case[1] -ge $r.Y -and $case[1] -lt ($r.Y + $r.Height)) {
                $mask = $mask -bor (1 -shl $i)
            }
        }
        $expectedMasks = @{ 0 = 0; 10 = 1; 12 = 2; 11 = 4; 15 = 8; 13 = 3; 14 = 6; 16 = 9; 17 = 12 }
        if ($mask -ne $expectedMasks[[int]$case[2]]) {
            throw "WinUI border regions disagree with native edge/corner hit at $($case[0]),$($case[1])."
        }
        $passed++
    }
    $point = [Windows.Graphics.PointInt32]::new($window.X, $window.Y)
    if ($resizeHit.Invoke($null, @($window, $point, $border, $true)) -ne 0) {
        throw 'Maximized windows must not expose resize handles.'
    }
    $passed++
    if ($resizeRegions.Invoke($null, @($size, $border, $true)).Length -ne 0) {
        throw 'Maximized windows must clear the WinUI resize regions.'
    }
    $passed++
}

foreach ($sizeValues in @(@(200, 200), @(600, 400), @(1200, 800), @(2400, 1600))) {
    $size = [Windows.Graphics.SizeInt32]::new($sizeValues[0], $sizeValues[1])
    $border = [Windows.Graphics.SizeInt32]::new(12, 16)
    $regions = $resizeRegions.Invoke($null, @($size, $border, $false))
    $expected = @(
        "0,0,12,$($size.Height)", "0,0,$($size.Width),16",
        "$($size.Width - 12),0,12,$($size.Height)", "0,$($size.Height - 16),$($size.Width),16"
    )
    for ($i = 0; $i -lt 4; $i++) {
        $r = $regions[$i]
        if ("$($r.X),$($r.Y),$($r.Width),$($r.Height)" -ne $expected[$i]) {
            throw 'Resize regions must track new window dimensions and independent X/Y border metrics.'
        }
        $passed++
    }
}

$hover = $geometry.GetMethod('GetHoverOpacity', $flags)
$fade = $geometry.GetMethod('FadeHoverOpacity', $flags)

function Assert-Opacity([string]$Name, [double]$Actual, [double]$Expected) {
    if ([Math]::Abs($Actual - $Expected) -gt 0.000001) {
        throw "${Name}: expected $Expected, got $Actual"
    }
    $script:passed++
}

foreach ($scale in @(1.0, 1.5, 2.0, 3.0)) {
    $window = Rect @(-1500, -800, (720 * $scale), (380 * $scale))
    foreach ($case in @(
        @(360, 190, 0.18), @(0, 190, 0.32), @(360, 0, 0.32),
        @(12, 190, 0.18), @(24, 190, 0.18), @(0, 0, 0.32),
        @(-1, 190, 0), @(720, 190, 0), @(360, -1, 0), @(360, 380, 0)
    )) {
        $point = [Windows.Graphics.PointInt32]::new(
            $window.X + $case[0] * $scale, $window.Y + $case[1] * $scale)
        Assert-Opacity "Hover at $($case[0]),$($case[1]), scale $scale" `
            ($hover.Invoke($null, @($window, $point, $scale, $true, $false, [double]0))) $case[2]
    }
    $point = [Windows.Graphics.PointInt32]::new($window.X + $window.Width - 1, $window.Y + $window.Height - 1)
    Assert-Opacity 'Bottom/right resize corner' ($hover.Invoke($null, @($window, $point, $scale, $true, $false, [double]0))) 0.32
    Assert-Opacity 'Another window covers the pointer' ($hover.Invoke($null, @($window, $point, $scale, $false, $false, [double]0))) 0
    Assert-Opacity 'Maximized edges do not advertise resizing' ($hover.Invoke($null, @($window, $point, $scale, $true, $true, [double]0))) 0.18

    foreach ($case in @(
        @(360, 190, 1499, 0.18), @(360, 190, 1500, 0), @(360, 190, 10000, 0),
        @(0, 190, 10000, 0.32), @(12, 190, 10000, 0.16), @(24, 190, 10000, 0),
        @(0, 0, 10000, 0.32), @(-1, 190, 10000, 0), @(720, 190, 10000, 0),
        @(360, 190, 0, 0.18)
    )) {
        $point = [Windows.Graphics.PointInt32]::new(
            $window.X + $case[0] * $scale, $window.Y + $case[1] * $scale)
        Assert-Opacity "Timed entry/edge cue at scale $scale, elapsed $($case[2])" `
            ($hover.Invoke($null, @($window, $point, $scale, $true, $false, [double]$case[2]))) $case[3]
    }
    $point = [Windows.Graphics.PointInt32]::new($window.X, $window.Y)
    Assert-Opacity 'Maximized entry cue expires even at an edge' `
        ($hover.Invoke($null, @($window, $point, $scale, $true, $true, [double]1500))) 0
}

foreach ($case in @(
    @(0, 0.32, 90, 0.16), @(0.32, 0, 90, 0.16),
    @(0, 0.18, 180, 0.18), @(0.32, 0, 180, 0),
    @(0.18, 0.32, 90, 0.32), @(0.32, 0.18, 90, 0.18),
    @(0.16, 0.18, 0, 0.16), @(0.16, 0.18, -1, 0.16),
    @(0.32, 0, 5000, 0), @(0, 0, 5000, 0)
)) {
    Assert-Opacity 'Fade timing, reversal, and clamping' `
        ($fade.Invoke($null, @([double]$case[0], [double]$case[1], [double]$case[2]))) $case[3]
}

[xml]$xaml = Get-Content (Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI\Experiences\CompactExperienceWindow.xaml') -Raw
$cue = $xaml.SelectSingleNode('//*[@*[local-name()="Name"]="WindowBoundaryCue"]')
if ($cue.Opacity -ne '0' -or $cue.IsHitTestVisible -ne 'False' -or $cue.'Canvas.ZIndex' -ne '4') {
    throw 'Boundary cue must start invisible, remain above content, and never intercept input.'
}
$passed++
foreach ($border in $cue.Border) {
    if ($border.BorderBrush -ne '{ThemeResource ShellTextPrimaryBrush}' -or $border.HasAttribute('Background')) {
        throw 'Boundary layers must follow live theme resources without painting the window interior.'
    }
    $passed++
}

Write-Output "Passed $passed compact window geometry and hover checks (no windows moved or opened)."
