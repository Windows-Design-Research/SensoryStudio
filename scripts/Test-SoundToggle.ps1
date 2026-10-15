#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI'
$usings = [System.Collections.Generic.HashSet[string]]::new()
[void]$usings.Add('using System;')
[void]$usings.Add('using System.IO;')
[void]$usings.Add('using System.Linq;')
[void]$usings.Add('using System.Collections.Generic;')

# Compile real settings, registry, interface, and pool code in memory against silent SDK stubs.
$parts = foreach ($file in @(
    'Services\AppSettingsService.cs',
    'Services\SoundSettingsService.cs',
    'Services\InteractiveSoundService.cs',
    'Experiences\IExperience.cs'
)) {
    $source = Get-Content (Join-Path $root $file) -Raw
    foreach ($match in [regex]::Matches($source, '(?m)^using .+;\r?$')) {
        [void]$usings.Add($match.Value.Trim())
    }
    $source = [regex]::Replace($source, '(?m)^using .+;\r?\n', '')
    [regex]::Replace($source, 'namespace ([\w.]+);', 'namespace $1 {') + "`n}"
}

function Get-AudioMethod([string]$source, [string]$name) {
    $match = [regex]::Match($source, "(?ms)^    (?:public|private) void $name\([^`n]*\)\r?`n    \{.*?^    \}")
    if (!$match.Success) { throw "Audio method not found: $name" }
    return $match.Value
}

$owners = @{}
foreach ($name in @('ZenGardenExperience.xaml')) {
    $owners[$name] = Get-Content (Join-Path $root "Experiences\$name.cs") -Raw
    if ($owners[$name] -notmatch 'IAudioExperience' -or
        $owners[$name] -notmatch 'SoundSettingsService.Register\(this\)' -or
        $owners[$name] -notmatch 'SoundSettingsService.Unregister\(this\)') {
        throw "$name must participate in the global audio lifecycle."
    }
}
foreach ($file in @('MainWindow.xaml.cs', 'Experiences\ExperienceWindow.xaml.cs')) {
    $window = Get-Content (Join-Path $root $file) -Raw
    if ($window -notmatch 'SoundSettingsService.SetSoundEnabled\(' -or
        $window -notmatch 'SoundSettingsService.Changed \+=' -or
        $window -notmatch 'SoundSettingsService.Changed -=') {
        throw "$file must use and unsubscribe from global sound settings."
    }
}
$garden = $owners['ZenGardenExperience.xaml']
if ($garden -notmatch 'window.__soundVolume=' -or
    $garden -notmatch 'CoreWebView2.IsMuted = _volume <= 0' -or
    $garden -notmatch 'if \(args.IsSuccess\)\s*\{\s*SetVolume\(_volume\)') {
    throw 'Zen Garden must apply bootstrap, native mute, and post-navigation volume.'
}

# Extract only audio methods from UI-heavy experiences; no XAML or timers are instantiated.
$tests = @'
namespace Microsoft.UI.Xaml.Controls { public class UserControl { } }
namespace Windows.Media.Core
{
    public class MediaSource
    {
        public static MediaSource CreateFromUri(Uri uri) => new();
    }
}
namespace Windows.Media.Playback
{
    public class MediaPlayer : IDisposable
    {
        public bool AutoPlay { get; set; }
        public double Volume { get; set; }
        public object? Source { get; set; }
        public Session PlaybackSession { get; } = new();
        public int Plays { get; private set; }
        public int Pauses { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool Disposed { get; private set; }
        public void Play() { Plays++; IsPlaying = true; }
        public void Pause() { Pauses++; IsPlaying = false; }
        public void Dispose() { Disposed = true; }
    }
    public class Session { public TimeSpan Position { get; set; } }
}
namespace SensoryStudio.SoundTests
{
    public class GardenAudio : IAudioExperience
    {
        private double _volume;
        public double Volume => _volume;
        public BrowserStub Browser { get; } = new();
        public readonly List<string> Messages = new();
        private void PostToGarden(object message) => Messages.Add(System.Text.Json.JsonSerializer.Serialize(message));
        __GARDEN_VOLUME__
    }
    public class BrowserStub { public CoreStub? CoreWebView2 { get; set; } }
    public class CoreStub { public bool IsMuted { get; set; } }
    public static class SoundTests
    {
        private static int _passed;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _passed++;
        }
        public static int Run()
        {
            AppSettingsService.Current.SoundVolume = 0.37;
            AppSettingsService.Current.SoundEnabled = false;
            var garden = new GardenAudio();
            var owners = new IAudioExperience[] { garden };
            foreach (var owner in owners) SoundSettingsService.Register(owner);
            Check(garden.Volume == 0, "Owners initialize muted");
            Check(garden.Messages.Count == 0, "Registration never starts playback");
            garden.Browser.CoreWebView2 = new CoreStub();
            garden.SetVolume(garden.Volume);
            Check(garden.Browser.CoreWebView2.IsMuted && garden.Messages[^1].Contains("\"value\":0"),
                "Pre-load mute is retained for WebView creation");

            int changed = 0;
            EventHandler onChanged = (_, _) => changed++;
            SoundSettingsService.Changed += onChanged;
            SoundSettingsService.SetSoundEnabled(true);
            Check(garden.Volume == 0.37 && !garden.Browser.CoreWebView2.IsMuted, "Enable reaches every owner");
            SoundSettingsService.Register(garden);

            using var pool = new InteractiveSoundService();
            pool.Initialize();
            var players = new[] { new MediaPlayer(), new MediaPlayer(), new MediaPlayer(), new MediaPlayer() };
            var pools = (Dictionary<string, MediaPlayer[]>)typeof(InteractiveSoundService)
                .GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(pool)!;
            var next = (Dictionary<string, int>)typeof(InteractiveSoundService)
                .GetField("_nextPlayer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(pool)!;
            pools["stone"] = players;
            next["stone"] = 0;
            for (int i = 0; i < 4; i++) pool.PlayStone();
            Check(players.All(player => player.IsPlaying && player.Volume == 0.37), "All pool voices play at saved volume");
            SoundSettingsService.SetSoundEnabled(false);
            Check(players.All(player => !player.IsPlaying && player.Volume == 0), "Mute stops every in-flight pool voice");
            Check(garden.Browser.CoreWebView2.IsMuted && garden.Messages[^1].Contains("\"value\":0"),
                "Garden gets immediate native mute and a volume message");
            pool.PlayStone();
            Check(players.Sum(player => player.Plays) == 4, "Muted interactive cues are skipped");
            Check(changed == 2 && AppSettingsService.Current.SoundVolume == 0.37, "Global toggle preserves saved volume");
            SoundSettingsService.SetSoundEnabled(false);
            Check(changed == 2, "Toggle synchronization is idempotent");
            SoundSettingsService.SetSoundEnabled(true);
            Check(players.All(player => !player.IsPlaying), "Re-enable does not resume any paused/cancelled audio");
            Check(players.All(player => player.Volume == 0.37),
                "Re-enable restores volume for existing players");
            pool.PlayStone();
            Check(players[0].Plays == 2,
                "Fresh cues work after re-enable");
            foreach (var owner in owners) SoundSettingsService.Unregister(owner);
            pool.Dispose();
            SoundSettingsService.SetSoundEnabled(false);
            Check(garden.Volume == 0.37,
                "Closed owners are no longer notified");
            Check(players.All(player => player.Disposed), "Pool disposal releases players");
            SoundSettingsService.Register(garden);
            Check(garden.Volume == 0, "Reactivation picks up current mute");
            SoundSettingsService.Unregister(garden);
            SoundSettingsService.Changed -= onChanged;
            return _passed;
        }
    }
}
'@
$tests = $tests.Replace('__GARDEN_VOLUME__', (Get-AudioMethod $garden 'SetVolume'))
[void]$usings.Add('using SensoryStudio.WinUI.Services;')
Add-Type -TypeDefinition ("#nullable enable`n" + ($usings -join "`n") + "`n" + ($parts -join "`n") + "`n" + $tests)
$passed = [SensoryStudio.SoundTests.SoundTests]::Run()
Write-Output "Passed $passed native sound checks plus lifecycle/host wiring checks (recording stubs; no app/audio/settings writes)."
