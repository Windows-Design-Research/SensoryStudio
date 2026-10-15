#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI'
$source = Get-Content (Join-Path $root 'Services\AppSettingsService.cs') -Raw
$source = $source.Replace(
    'namespace SensoryStudio.WinUI.Services;',
    'namespace SensoryStudio.WinUI.Services {'
) + "`n}"

$tests = @'
namespace SensoryStudio.WinUI.Services
{
    public static class SettingsTests
    {
        private static int _passed;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _passed++;
        }

        public static int Run(string directory)
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            AppSettings settings = store.Load();
            Check(store.Notice == null && settings.SoundVolume == 0.6 &&
                settings.HapticsIntensity == 1, "Missing settings use unchanged defaults without a notice.");
            Check(!File.Exists(path), "Loading defaults must not create a file.");
            Check(store.Save(settings), "First save creates the file.");
            Check(store.Notice == null, "A successful first save is silent.");
            using (var defaults = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Check(!defaults.RootElement.EnumerateObject().Any(property => property.Name.StartsWith("Pomodoro")),
                    "New settings do not create preferences for retired experiences.");
            }

            string valid = """
                {"SoundEnabled":false,"SoundVolume":0.37,"HapticsEnabled":false,
                 "HapticsIntensity":0.42,"PomodoroDurationMinutes":5,
                 "PomodoroAlarm":"Alarm10.wav","PomodoroMinuteHapticsEnabled":false,
                 "PomodoroDisplayStyle":"Outline",
                 "WindowBounds":{"bubbles":{"Left":-1200.4,"Top":100.6,"Width":900.4,"Height":600.6}},
                 "ZenGardenPlantLayout":"{\"grass\":[]}","FutureSetting":{"items":[1,2,3]}}
                """;
            File.WriteAllText(path, valid);
            settings = store.Load();
            Check(store.Notice == null, "Valid settings must not show recovery.");
            Check(!settings.SoundEnabled && settings.SoundVolume == 0.37 &&
                !settings.HapticsEnabled && settings.HapticsIntensity == 0.42,
                "Every active preference is retained.");
            Check(settings.AdditionalSettings!["PomodoroDurationMinutes"].GetInt32() == 5 &&
                settings.AdditionalSettings["PomodoroAlarm"].GetString() == "Alarm10.wav" &&
                !settings.AdditionalSettings["PomodoroMinuteHapticsEnabled"].GetBoolean() &&
                settings.AdditionalSettings["PomodoroDisplayStyle"].GetString() == "Outline",
                "Retired preferences remain inert extension data without resetting valid settings.");
            Check(settings.WindowBounds["bubbles"].Left == -1200.4 &&
                settings.WindowBounds["bubbles"].Height == 600.6, "Valid saved bounds are unchanged.");
            Check(store.Save(settings), "Replace an existing settings file.");
            Check(store.Notice == null, "Successful replacement is silent.");
            using (var saved = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Check(saved.RootElement.GetProperty("ZenGardenPlantLayout").GetString() == "{\"grass\":[]}",
                    "Settings from newer app versions must survive saves.");
                Check(saved.RootElement.GetProperty("FutureSetting").GetProperty("items").GetArrayLength() == 3,
                    "Unknown nested settings must survive saves.");
                Check(!saved.RootElement.TryGetProperty("EffectiveSoundVolume", out _),
                    "Derived settings are not persisted.");
                Check(saved.RootElement.GetProperty("PomodoroDurationMinutes").GetInt32() == 5 &&
                    saved.RootElement.GetProperty("PomodoroAlarm").GetString() == "Alarm10.wav" &&
                    !saved.RootElement.GetProperty("PomodoroMinuteHapticsEnabled").GetBoolean() &&
                    saved.RootElement.GetProperty("PomodoroDisplayStyle").GetString() == "Outline",
                    "Saving active preferences does not erase historic preferences.");
            }
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Successful save leaves no temp files.");
            foreach (string? layout in new string?[] {
                null, "", " ", "{\"grass\":[],\"sandLines\":[[0.1,0.2]],\"decorativePebbles\":[]}"
            })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { ZenGardenPlantLayout = layout, SoundVolume = 0.37 }));
                settings = store.Load();
                Check(settings.ZenGardenPlantLayout == layout && settings.SoundVolume == 0.37 && store.Notice == null,
                    "Main's nullable and valid saved garden layouts remain unchanged.");
                Check(store.Save(settings) && store.Load().ZenGardenPlantLayout == layout && store.Notice == null,
                    "Garden layouts round-trip without losing placement data or introducing prompts.");
            }
            foreach (string invalidLayout in new[] { "42", "{}", "\"{\"", "\"[]\"", "\"null\"" })
            {
                File.WriteAllText(path, "{\"SoundVolume\":0.37,\"ZenGardenPlantLayout\":" + invalidLayout + "}");
                settings = store.Load();
                Check(settings.ZenGardenPlantLayout == null && settings.SoundVolume == 0.37 &&
                    store.Notice?.Kind == SettingsNoticeKind.Recovered,
                    "An invalid garden layout is recovered without losing other valid preferences.");
                Check(store.Save(settings) && store.Load().ZenGardenPlantLayout == null && store.Notice == null,
                    "A recovered garden layout can be saved and subsequently loaded normally.");
            }
            foreach (var encoding in new System.Text.Encoding[] {
                new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode
            })
            {
                File.WriteAllText(path, valid, encoding);
                Check(store.Load().SoundVolume == 0.37 && store.Notice == null,
                    "Previously accepted byte-order-mark encodings remain readable.");
            }

            foreach (string json in new[] { "", "{", "null", "[]", "\"text\"", "{\"SoundVolume\":" })
            {
                File.WriteAllText(path, json);
                settings = store.Load();
                Check(settings.SoundVolume == 0.6 && store.Notice?.Kind == SettingsNoticeKind.Recovered,
                    "Invalid JSON/root recovers with an explicit notice.");
                Check(File.ReadAllText(store.Notice!.Detail) == json, "Recovery preserves the original bytes.");
                Check(File.ReadAllText(path) == json, "Load does not overwrite the original.");
                Check(store.Save(settings), "Saving recovered settings is allowed after preservation.");
            }

            foreach (string invalid in new[] {
                "\"SoundVolume\":-1", "\"SoundVolume\":2", "\"SoundVolume\":1e999",
                "\"SoundVolume\":\"bad\"", "\"SoundVolume\":null",
                "\"HapticsIntensity\":-1", "\"HapticsIntensity\":2",
                "\"HapticsEnabled\":null", "\"HapticsEnabled\":\"true\"",
                "\"WindowBounds\":null", "\"WindowBounds\":[]"
            })
            {
                string json = "{\"SoundEnabled\":false," + invalid + "}";
                File.WriteAllText(path, json);
                settings = store.Load();
                Check(store.Notice?.Kind == SettingsNoticeKind.Recovered && !settings.SoundEnabled,
                    "Repair only invalid preferences; preserve other valid preferences.");
                Check(settings.SoundVolume == 0.6 && settings.HapticsIntensity == 1 &&
                    settings.WindowBounds.Count == 0,
                    "Invalid values recover to existing defaults.");
                Check(File.ReadAllText(store.Notice!.Detail) == json, "Normalized input is backed up.");
            }

            foreach (string retired in new[] {
                "\"PomodoroDurationMinutes\":0", "\"PomodoroDurationMinutes\":\"5\"",
                "\"PomodoroAlarm\":null", "\"PomodoroAlarm\":\"..\\\\secret.wav\"",
                "\"PomodoroDisplayStyle\":\"unknown\"", "\"PomodoroMinuteHapticsEnabled\":[]"
            })
            {
                File.WriteAllText(path, "{\"SoundVolume\":0.37," + retired + "}");
                settings = store.Load();
                Check(settings.SoundVolume == 0.37 && store.Notice == null && store.Save(settings),
                    "Unused historic preferences are opaque data, not a reason to recover active settings.");
                using var expected = JsonDocument.Parse("{" + retired + "}");
                using var saved = JsonDocument.Parse(File.ReadAllText(path));
                JsonProperty property = expected.RootElement.EnumerateObject().Single();
                Check(saved.RootElement.GetProperty(property.Name).GetRawText() == property.Value.GetRawText(),
                    "Historic values round-trip without use or reinterpretation.");
            }
            foreach (double level in new[] { 0d, 0.37, 1d })
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new { SoundVolume = level, HapticsIntensity = level }));
                settings = store.Load();
                Check(settings.SoundVolume == level && settings.HapticsIntensity == level && store.Notice == null,
                    "Valid intensity/volume endpoints are preserved.");
            }
            File.WriteAllText(path, """{"SoundVolume":0.1,"SoundVolume":0.7}""");
            Check(store.Load().SoundVolume == 0.7 && store.Notice == null,
                "Duplicate properties retain last-value behavior.");

            File.WriteAllText(path, """
                {"WindowBounds":{
                    "valid":{"Left":-1600,"Top":20,"Width":600,"Height":400},
                    "missing":null,"badType":{"Width":"wide"},
                    "small":{"Left":0,"Top":0,"Width":199,"Height":400},
                    "overflow":{"Left":2147483647,"Top":0,"Width":600,"Height":400}
                }}
                """);
            settings = store.Load();
            Check(settings.WindowBounds.Count == 1 && settings.WindowBounds.ContainsKey("valid") &&
                store.Notice?.Kind == SettingsNoticeKind.Recovered,
                "Invalid bounds cannot discard a valid window's placement.");

            string prefix = "{\"FutureSetting\":\"";
            string suffix = "\"}";
            string atLimit = prefix + new string('a', SettingsStore.MaximumFileBytes - prefix.Length - suffix.Length) + suffix;
            File.WriteAllText(path, atLimit);
            Check(store.Load().AdditionalSettings!["FutureSetting"].GetString()!.Length > 0 &&
                store.Notice == null, "The exact byte limit is accepted.");
            File.WriteAllText(path, atLimit + " ");
            settings = store.Load();
            Check(store.Notice?.Kind == SettingsNoticeKind.Recovered &&
                new FileInfo(store.Notice.Detail).Length == SettingsStore.MaximumFileBytes + 1,
                "Oversized files recover without losing the original.");

            File.WriteAllText(path, valid);
            settings = store.Load();
            int notices = 0;
            store.NoticeChanged += (_, _) => notices++;
            settings.SoundVolume = double.NaN;
            Check(!store.Save(settings) && store.Notice?.Kind == SettingsNoticeKind.SaveFailed,
                "Non-finite in-memory settings fail explicitly.");
            Check(File.ReadAllText(path) == valid, "Failed serialization preserves the last good file.");
            settings.SoundVolume = 99;
            Check(!store.Save(settings) && File.ReadAllText(path) == valid,
                "Invalid in-memory ranges must not replace saved settings.");
            settings.SoundVolume = 0.37;
            settings.AdditionalSettings!["Huge"] = JsonSerializer.SerializeToElement(atLimit);
            Check(!store.Save(settings) && File.ReadAllText(path) == valid,
                "Oversized saves preserve the last good file.");
            settings.AdditionalSettings.Remove("Huge");
            Check(store.Save(settings) && store.Notice == null, "Successful retry clears a save failure.");
            Check(notices >= 3, "Failures are surfaced to subscribers.");

            string beforeLock = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                settings.SoundVolume = 0.5;
                Check(!store.Save(settings) && store.Notice?.Kind == SettingsNoticeKind.SaveFailed,
                    "A locked destination cannot be replaced.");
                Check(File.ReadAllText(path) == beforeLock, "Replacement failure keeps original bytes.");
                int beforeRetry = notices;
                Check(!store.Save(settings) && notices == beforeRetry,
                    "Identical repeated errors do not repeatedly interrupt the user.");
            }
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed replacement cleans temporary files.");
            Check(store.Save(settings) && store.Notice == null, "Saving recovers after the lock is released.");

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                store.Load();
                Check(store.Notice?.Kind == SettingsNoticeKind.ReadFailed && !store.Save(new AppSettings()),
                    "Unreadable settings are not overwritten by session defaults.");
            }
            Check(!store.Save(new AppSettings()), "Write protection survives until a successful reload.");
            store.Load();
            Check(store.Save(new AppSettings()), "Successful reload permits saves again.");

            string obstructed = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(obstructed, "preserve");
            var blocked = new SettingsStore(Path.Combine(obstructed, "settings.json"));
            Check(!blocked.Save(new AppSettings()) && blocked.Notice?.Kind == SettingsNoticeKind.SaveFailed &&
                File.ReadAllText(obstructed) == "preserve", "First-save IO failure is explicit and nondestructive.");
            return _passed;
        }

        public static void CheckBackupFailure(string directory)
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            var settings = store.Load();
            if (store.Notice?.Kind != SettingsNoticeKind.RecoveryFailed ||
                settings.SoundVolume != 0.6 || store.Save(settings) ||
                File.ReadAllText(path) != "{")
                throw new Exception("Failed backup must leave original data protected and report recovery failure.");
        }
    }
}
'@

Add-Type -TypeDefinition ("#nullable enable`nusing System;`nusing System.IO;`nusing System.Linq;`nusing System.Collections.Generic;`n" + $source + "`n" + $tests)
$directory = Join-Path ([IO.Path]::GetTempPath()) "SensoryStudio-settings-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $directory | Out-Null
try {
    $passed = [SensoryStudio.WinUI.Services.SettingsTests]::Run($directory)
    $backupFailureDirectory = Join-Path $directory 'backup-failure'
    New-Item -ItemType Directory -Path $backupFailureDirectory | Out-Null
    [IO.File]::WriteAllText((Join-Path $backupFailureDirectory 'settings.json'), '{')
    $originalAcl = Get-Acl -LiteralPath $backupFailureDirectory
    $restrictedAcl = Get-Acl -LiteralPath $backupFailureDirectory
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $identity, [Security.AccessControl.FileSystemRights]::CreateFiles,
        [Security.AccessControl.AccessControlType]::Deny)
    $restrictedAcl.AddAccessRule($rule)
    try {
        Set-Acl -LiteralPath $backupFailureDirectory -AclObject $restrictedAcl
        [SensoryStudio.WinUI.Services.SettingsTests]::CheckBackupFailure($backupFailureDirectory)
        $passed++
    } finally {
        Set-Acl -LiteralPath $backupFailureDirectory -AclObject $originalAcl
    }

    $app = Get-Content (Join-Path $root 'App.xaml.cs') -Raw
    if ($app.IndexOf('AppSettingsService.NoticeChanged +=') -lt 0 -or
        $app.IndexOf('AppSettingsService.NoticeChanged +=') -gt $app.IndexOf('AppSettingsService.Load()')) {
        throw 'Startup must subscribe to settings notices before loading.'
    }
    [xml]$resources = Get-Content (Join-Path $root 'Strings\en-US\Resources.resw') -Raw
    foreach ($kind in @('Recovered', 'ReadFailed', 'RecoveryFailed', 'SaveFailed')) {
        if (-not $resources.SelectSingleNode("//data[@name='Settings${kind}Message']/value").InnerText) {
            throw "Missing localized settings notice: $kind"
        }
    }
    Write-Output "Passed $passed settings persistence checks plus notification wiring/resources (isolated files; no real settings, app, or dialogs used)."
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
