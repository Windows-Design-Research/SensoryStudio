#Requires -Version 7.4
param(
    [string]$PublishDirectory,
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture
)

$ErrorActionPreference = 'Stop'
if ($Architecture -and -not $PublishDirectory) {
    throw '-Architecture requires -PublishDirectory.'
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = Join-Path $repo 'src\SensoryStudio.WinUI'

$retiredTypes = 'Breathing|Bubbles|Strings|Minesweeper|Ambient|Rain|SensoryField|Pomodoro|PulseRunner'
foreach ($file in Get-ChildItem (Join-Path $root 'Experiences') -File) {
    if ($file.Name -match "^($retiredTypes)Experience") {
        throw "Retired experience source remains: $($file.Name)"
    }
}
$implementations = @(Get-ChildItem (Join-Path $root 'Experiences') -Filter '*.cs' |
    Where-Object { (Get-Content $_.FullName -Raw) -match 'class \w+.*\bIExperience\b' } |
    ForEach-Object { [regex]::Match((Get-Content $_.FullName -Raw), 'class (\w+).*\bIExperience\b').Groups[1].Value } |
    Sort-Object)
if (($implementations -join ',') -ne 'PowderOrbExperience,ZenGardenExperience') {
    throw "Expected exactly two experience implementations, found: $($implementations -join ', ')"
}
$hotkeys = Get-Content (Join-Path $root 'Services\NativeHotkeyService.cs') -Raw
$registrations = [regex]::Matches($hotkeys, 'Register\(\d+,.*?\);')
if ($registrations.Count -ne 1 -or
    $registrations[0].Value -ne 'Register(9005, "zensandart", ModControl | ModShift | ModNoRepeat, 0x45);') {
    throw 'Only the original Ctrl+Shift+E garden shortcut may remain; retired shortcuts must not be remapped.'
}
foreach ($path in @('MainWindow.xaml', 'MainWindow.xaml.cs',
    'Experiences\CompactExperienceWindow.xaml.cs', 'Experiences\ExperienceWindow.xaml.cs',
    'Experiences\IExperience.cs')) {
    if ((Get-Content (Join-Path $root $path) -Raw) -match
        "($retiredTypes)Experience|IBreathingSettings|IStringSettings|AmbientSelector|MinesweeperSelector") {
        throw "Retired UI/settings branch remains: $path"
    }
}
foreach ($path in @(
    'src\SensoryStudio.WinUI\Experiences\PomodoroExperience.cs',
    'src\SensoryStudio.WinUI\Experiences\PomodoroOrbDisplay.cs',
    'src\SensoryStudio.WinUI\Experiences\PulseRunnerExperience.cs',
    'src\SensoryStudio.WinUI\Controls\PomodoroModeSelector.cs',
    'Assets\icons\pomodoro.png', 'Assets\icons\pomodoro thumbnail.svg',
    'Assets\icons\pulserunner.png', 'Assets\icons\pulse runner thumbnail.svg',
    'src\SensoryStudio.WinUI\Controls\MinesweeperNumber.cs',
    'src\SensoryStudio.WinUI\Services\SevenSegmentDigits.cs',
    'src\SensoryStudio.WinUI\Assets\LiquidEther\liquid-ether.html'
)) {
    if (Test-Path -LiteralPath (Join-Path $repo $path)) {
        throw "Retired experience source/asset remains: $path"
    }
}
$retiredAssetPattern = '(?i)^(breathe|breathing|bubble|strings?|zenstrum|mine[s]?weeper|ambient|ocean|rain|stream|fireplace|fire Thumbnail|whitenoise|calming|pulserunner|pulse runner|pomodoro|LiquidEther)'
foreach ($file in Get-ChildItem (Join-Path $repo 'Assets') -Recurse -File) {
    if ($file.Name -match $retiredAssetPattern -or $file.Extension -eq '.mp3') {
        throw "Retired asset remains: $($file.FullName)"
    }
}
[xml]$project = Get-Content (Join-Path $root 'SensoryStudio.WinUI.csproj') -Raw
if ($project.SelectNodes('//PackageReference[@Include="Microsoft.Graphics.Win2D"]').Count) {
    throw 'The retired renderer must not retain the Win2D dependency.'
}
foreach ($path in @('MainWindow.xaml', 'Experiences\CompactExperienceWindow.xaml')) {
    [xml]$markup = Get-Content (Join-Path $root $path) -Raw
    $durationTags = @($markup.SelectNodes('//*[local-name()="ToggleMenuFlyoutItem"]') |
        Where-Object { $_.GetAttribute('Click') -in @('ExperienceDurationItem_Click', 'DurationItem_Click') } |
        ForEach-Object { $_.GetAttribute('Tag') })
    if (($durationTags -join ',') -ne '0,1,3,5') {
        throw "Visible session durations changed: $path"
    }
}

$usings = [System.Collections.Generic.HashSet[string]]::new()
foreach ($name in @('System', 'System.Linq', 'System.Collections.Generic')) {
    [void]$usings.Add("using $name;")
}
$parts = foreach ($path in @('Models\ExperienceCard.cs', 'Services\ExperienceLauncher.cs')) {
    $source = Get-Content (Join-Path $root $path) -Raw
    foreach ($match in [regex]::Matches($source, '(?m)^using .+;\r?$')) {
        [void]$usings.Add($match.Value.Trim())
    }
    $source = [regex]::Replace($source, '(?m)^using .+;\r?\n', '')
    [regex]::Replace($source, 'namespace ([\w.]+);', 'namespace $1 {') + "`n}"
}

# Run the real catalog and launcher with inert constructors, not native UI/devices.
$stubsAndTests = @'
namespace SensoryStudio.WinUI.Experiences
{
    public interface IExperience { }
    public sealed class PowderOrbExperience : IExperience { }
    public sealed class ZenGardenExperience : IExperience { }
}
namespace SensoryStudio.WinUI
{
    public static class CatalogTests
    {
        private static int _passed;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _passed++;
        }
        public static int Run()
        {
            var cards = Models.ExperienceCatalog.All;
            Check(cards.Select(card => card.Id).SequenceEqual(new[] {
                "powderorb", "zengardenhaptic"
            }), "The catalog contains only Power Orb and Zen Garden, in their original relative order.");
            Check(cards.Select(card => card.DisplayName).SequenceEqual(new[] { "Power Orb", "Zen Garden" }),
                "Retained display names must not change.");
            Check(cards.Where(card => card.Category == Models.ExperienceCategory.Focus)
                .Select(card => card.Id).SequenceEqual(new[] { "powderorb" }),
                "The Focus filter contains Power Orb.");
            Check(cards.Where(card => card.Category == Models.ExperienceCategory.TakeABreak)
                .Select(card => card.Id).SequenceEqual(new[] { "zengardenhaptic" }),
                "The Take a break filter contains Zen Garden.");
            foreach (var card in cards)
            {
                Check(card.SelectedDurationMinutes == 3 && card.SelectedDurationTag == "3",
                    "Existing session defaults must remain unchanged.");
                int changes = 0;
                card.PropertyChanged += (_, _) => changes++;
                card.SelectedDurationTag = "0";
                Check(card.SelectedDurationMinutes == 0 && changes == 2,
                    "Unlimited duration must still notify both bindings.");
                card.SelectedDurationTag = "0";
                Check(changes == 2, "Repeated duration selection is unchanged.");
                card.SelectedDurationTag = "3";
            }
            var routes = new Dictionary<string, string>
            {
                ["powderorb"] = "PowderOrbExperience", ["zengardenhaptic"] = "ZenGardenExperience",
                ["zensandart"] = "ZenGardenExperience", ["zengarden3d"] = "ZenGardenExperience",
            };
            foreach (var route in routes)
            {
                Check(Services.ExperienceLauncher.Create(route.Key).GetType().Name == route.Value,
                    "Every retained catalog and garden alias must resolve.");
                Check(Services.ExperienceLauncher.Create(route.Key.ToUpperInvariant()).GetType().Name == route.Value,
                    "Retained routes must remain case-insensitive.");
            }
            foreach (string removed in new[] { "pomodoro", "pulserunner", "breathe", "zenstrum", "bubbles",
                "minesweeper", "ambient", "ocean", "rain", "stream", "fireplace", "white-noise",
                "aquasweep", "calmingrain", "calm", "energized", "grounded", "unknown" })
            foreach (string id in new[] { removed, removed.ToUpperInvariant() })
            {
                try
                {
                    Services.ExperienceLauncher.Create(id);
                    throw new Exception("Removed/unknown routes must not launch.");
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    Check(exception.ParamName == "experienceId" && Equals(exception.ActualValue, id),
                        "Removed IDs use the existing explicit unknown-experience error.");
                }
            }
            return _passed;
        }
    }
}
'@
Add-Type -TypeDefinition ("#nullable enable`n" + ($usings -join "`n") + "`n" +
    ($parts -join "`n") + "`n" + $stubsAndTests)
$passed = [SensoryStudio.WinUI.CatalogTests]::Run()
foreach ($card in [SensoryStudio.WinUI.Models.ExperienceCatalog]::All) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo "Assets\icons\$($card.ThumbnailFile)"))) {
        throw "A retained card thumbnail is missing: $($card.Id)"
    }
}

if ($PublishDirectory) {
    $output = (Resolve-Path -LiteralPath $PublishDirectory).Path
    if ($Architecture) {
        $expectedMachine = if ($Architecture -eq 'x64') { 0x8664 } else { 0xAA64 }
        foreach ($name in @('SensoryStudio.WinUI.exe', 'coreclr.dll', 'Microsoft.ui.xaml.dll')) {
            $nativeStream = [System.IO.File]::OpenRead((Join-Path $output $name))
            $nativePe = [System.Reflection.PortableExecutable.PEReader]::new($nativeStream)
            try {
                if ([int]$nativePe.PEHeaders.CoffHeader.Machine -ne $expectedMachine) {
                    throw "Published $name does not match the expected $Architecture architecture."
                }
            }
            finally {
                $nativePe.Dispose()
                $nativeStream.Dispose()
            }
        }
        Write-Output "Published apphost, .NET runtime, and WinUI runtime match $Architecture."
    }
    $deps = Get-Content (Join-Path $output 'SensoryStudio.WinUI.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    if (@($deps.libraries.Keys | Where-Object { $_ -like 'Microsoft.Graphics.Win2D/*' }).Count) {
        throw 'Win2D is still in the published dependency graph.'
    }
    $retired = @(Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object {
        $_.Name -match '(?i)Win2D|Microsoft\.Graphics\.Canvas' -or
        ($_.FullName.StartsWith((Join-Path $output 'Assets') + [IO.Path]::DirectorySeparatorChar) -and
            ($_.Name -match $retiredAssetPattern -or $_.Extension -eq '.mp3'))
    })
    if ($retired.Count) {
        throw "Retired payload files remain: $($retired.FullName -join ', ')"
    }
    $stream = [System.IO.File]::OpenRead((Join-Path $output 'SensoryStudio.WinUI.dll'))
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($handle in $metadata.AssemblyReferences) {
            $name = $metadata.GetString($metadata.GetAssemblyReference($handle).Name)
            if ($name -match 'Win2D|Microsoft\.Graphics\.Canvas') {
                throw "Compiled app still references the retired renderer: $name"
            }
        }
        $compiledExperiences = @()
        foreach ($handle in $metadata.TypeDefinitions) {
            $definition = $metadata.GetTypeDefinition($handle)
            $name = $metadata.GetString($definition.Name)
            if ($name -match "$retiredTypes|IIdleOnPointerExitExperience|IBreathingSettings|IStringSettings|SevenSegmentDigits") {
                throw "Compiled app still contains a retired experience type: $name"
            }
            if ($metadata.GetString($definition.Namespace) -eq 'SensoryStudio.WinUI.Experiences' -and
                $name -match 'Experience$' -and -not ($definition.Attributes -band [Reflection.TypeAttributes]::Interface)) {
                $compiledExperiences += $name
            }
        }
        if ((($compiledExperiences | Sort-Object) -join ',') -ne 'PowderOrbExperience,ZenGardenExperience') {
            throw "Compiled output must contain exactly the two retained implementations: $compiledExperiences"
        }
    }
    finally {
        $pe.Dispose()
        $stream.Dispose()
    }
    foreach ($file in @((Get-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.txt'))) +
        @(Get-ChildItem (Join-Path $repo 'ThirdPartyNotices') -Recurse -File) +
        @(Get-ChildItem (Join-Path $repo 'Assets\ZenGarden') -Recurse -File)) {
        $relative = [IO.Path]::GetRelativePath($repo, $file.FullName)
        $copied = Join-Path $output $relative
        if (-not (Test-Path -LiteralPath $copied -PathType Leaf) -or
            (Get-FileHash -LiteralPath $copied).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Retained garden asset or notice is missing/changed in output: $relative"
        }
    }
    Write-Output 'Output contains exactly two experience types, no retired payload, and byte-identical garden assets/notices.'
}
Write-Output "Passed $passed catalog/launcher checks plus source, thumbnail, and session-duration guards (no native UI)."
