#Requires -Version 7.4

param(
    [string]$BuildOutput = (Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0')
)

$ErrorActionPreference = 'Stop'
$BuildOutput = (Resolve-Path $BuildOutput).Path
[void][Reflection.Assembly]::LoadFrom((Join-Path $BuildOutput 'WinRT.Runtime.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $BuildOutput 'Microsoft.Windows.SDK.NET.dll'))
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $BuildOutput 'SensoryStudio.WinUI.dll'))
$placement = $assembly.GetType('SensoryStudio.WinUI.Services.WindowPlacementService', $true)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$calculate = $placement.GetMethod('CalculateBounds', $flags)
$getSaved = $placement.GetMethod('GetSavedBounds', $flags)
$boundsType = $assembly.GetType('SensoryStudio.WinUI.Services.WindowBounds', $true)
$passed = 0

function Assert-Placement {
    param(
        [string]$Name,
        [object]$Saved,
        [int[]]$DefaultSize,
        [int[]]$WorkArea,
        [bool]$PreservePosition,
        [int[]]$Expected,
        [int]$MinimumSize = 200
    )

    $savedRect = if ($null -eq $Saved) { $null } else {
        [Windows.Graphics.RectInt32]::new($Saved[0], $Saved[1], $Saved[2], $Saved[3])
    }
    $size = [Windows.Graphics.SizeInt32]::new($DefaultSize[0], $DefaultSize[1])
    $work = [Windows.Graphics.RectInt32]::new($WorkArea[0], $WorkArea[1], $WorkArea[2], $WorkArea[3])
    $actual = $calculate.Invoke($null, @($savedRect, $size, $work, $PreservePosition, $MinimumSize))
    $values = @($actual.X, $actual.Y, $actual.Width, $actual.Height)
    if (($values -join ',') -ne ($Expected -join ',')) {
        throw "${Name}: expected $($Expected -join ','), got $($values -join ',')"
    }
    $script:passed++
}

Assert-Placement 'Quarter-screen default on primary' $null @(960, 520) @(0, 0, 1920, 1040) $false @(480, 260, 960, 520)
Assert-Placement 'Quarter-screen default on left monitor' $null @(960, 520) @(-1920, 0, 1920, 1040) $false @(-1440, 260, 960, 520)
Assert-Placement 'Monitor above primary, with taskbar offset' $null @(800, 450) @(0, -1160, 1920, 1120) $false @(560, -825, 800, 450)
Assert-Placement 'Remember position on owner monitor' @(-1750, 100, 900, 600) @(960, 520) @(-1920, 0, 1920, 1040) $true @(-1750, 100, 900, 600)
Assert-Placement 'Move saved size from primary to left monitor' @(300, 100, 900, 600) @(960, 520) @(-1920, 0, 1920, 1040) $false @(-1410, 220, 900, 600)
Assert-Placement 'Move saved size from left to primary' @(-1700, 100, 900, 600) @(960, 520) @(0, 0, 1920, 1040) $false @(510, 220, 900, 600)
Assert-Placement 'Oversized saved window on smaller monitor' @(3000, 100, 2560, 1440) @(960, 520) @(0, 40, 1280, 680) $false @(0, 40, 1280, 680)
Assert-Placement 'Clamp saved right and bottom edges' @(1800, 900, 600, 400) @(600, 400) @(0, 40, 1920, 1000) $true @(1320, 640, 600, 400)
Assert-Placement 'Clamp saved left and top edges' @(-2000, -100, 600, 400) @(600, 400) @(-1920, 40, 1920, 1000) $true @(-1920, 40, 600, 400)
Assert-Placement 'Constrain default size to usable area' $null @(840, 580) @(1920, 0, 640, 480) $false @(1920, 0, 640, 480)
Assert-Placement 'Apply garden minimum before positioning' @(1800, 900, 200, 200) @(640, 480) @(0, 0, 1920, 1040) $true @(1520, 640, 400, 400) 400
Assert-Placement 'Usable area takes precedence over minimum' $null @(640, 480) @(-300, 0, 300, 250) $false @(-300, 0, 300, 250) 400
Assert-Placement 'Saved resizing overrides quarter-screen default' @(100, 100, 1100, 700) @(960, 520) @(0, 0, 1920, 1040) $true @(100, 100, 1100, 700)

foreach ($invalid in @(
    @([double]::NaN, 0, 600, 400),
    @(0, [double]::PositiveInfinity, 600, 400),
    @(0, 0, [double]::MaxValue, 400),
    @([double]::MinValue, 0, 600, 400),
    @(0, 0, 199, 400),
    @(0, 0, 600, -1),
    @([int]::MaxValue, 0, 600, 400),
    @(0, [int]::MaxValue, 600, 400)
)) {
    $saved = [Activator]::CreateInstance($boundsType)
    $saved.Left = $invalid[0]
    $saved.Top = $invalid[1]
    $saved.Width = $invalid[2]
    $saved.Height = $invalid[3]
    if ($null -ne $getSaved.Invoke($null, @($saved))) {
        throw "Invalid saved bounds were accepted: $($invalid -join ',')"
    }
    $passed++
}

if ($null -ne $getSaved.Invoke($null, @($null))) {
    throw 'Missing saved bounds must use the default.'
}
$passed++

$saved = [Activator]::CreateInstance($boundsType)
$saved.Left = -1600.4
$saved.Top = 100.6
$saved.Width = 900.4
$saved.Height = 600.6
$rounded = $getSaved.Invoke($null, @($saved))
if ((@($rounded.X, $rounded.Y, $rounded.Width, $rounded.Height) -join ',') -ne '-1600,101,900,601') {
    throw 'Valid saved bounds were not rounded correctly.'
}
$passed++

Write-Output "$passed placement checks passed."
