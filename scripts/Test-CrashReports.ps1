#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..\src\SensoryStudio.WinUI'
$source = Get-Content (Join-Path $root 'Services\CrashReportStore.cs') -Raw
$source = $source.Replace('namespace SensoryStudio.WinUI.Services;', 'namespace SensoryStudio.WinUI.Services {') + "`n}"
$app = Get-Content (Join-Path $root 'App.xaml.cs') -Raw
if ($app -notmatch 'CrashReports.Write\(args.Exception\)' -or
    $app -notmatch 'CrashReports.Maintain\(\)' -or
    $app -match 'AppendAllText|args.Handled\s*=') {
    throw 'Startup must maintain the crash report without appending raw exceptions or suppressing crashes.'
}
$haptics = Get-Content (Join-Path $root 'Services\HapticService.cs') -Raw
if ($haptics -match 'ControllerId=|CurrentHapticsController\?\.Id|ex.Message' -or
    $haptics -notmatch '\[Conditional\("DEBUG"\)\]\s*public void LogRouting') {
    throw 'Haptic routing traces must remain debug-only and omit device IDs and exception messages.'
}
$settings = Get-Content (Join-Path $root 'Services\AppSettingsService.cs') -Raw
if ($settings -notmatch 'Trace.TraceWarning\(\$"Settings notice: \{kind\}\."\)' -or
    $settings -notmatch 'new SettingsNotice\(kind, detail\)') {
    throw 'Settings traces must omit private details while preserving the local user notice.'
}

$tests = @'
namespace SensoryStudio.WinUI.Services
{
    public static class CrashReportTests
    {
        private static int _passed;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _passed++;
        }
        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }
        private sealed class PrivateException : Exception
        {
            public PrivateException() : base(@"DO_NOT_LOG C:\Users\private\secret.txt token=private@example.invalid")
            {
                HResult = unchecked((int)0x80070005);
                Data["PRIVATE_DATA"] = "DO_NOT_LOG";
                HelpLink = "https://example.invalid/DO_NOT_LOG";
                Source = "DO_NOT_LOG";
            }
            public override string Message => throw new InvalidOperationException("Message must not be read.");
            public override string? StackTrace => throw new InvalidOperationException("StackTrace text must not be read.");
            public override string ToString() => throw new InvalidOperationException("ToString must not be called.");
        }
        private sealed class GenericException<T> : Exception { }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining |
            System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
        private static void ThrowPrivate(int depth)
        {
            if (depth > 0) ThrowPrivate(depth - 1);
            else throw new PrivateException();
        }
        private static Exception Capture()
        {
            try { ThrowPrivate(20); }
            catch (PrivateException exception) { return exception; }
            throw new Exception("Expected fixture exception.");
        }
        public static int Run(string directory)
        {
            string path = Path.Combine(directory, "PRIVATE_PATH.crash.log");
            string settings = Path.Combine(directory, "settings.json");
            string backup = Path.Combine(directory, "settings.json.recovery-owner.json");
            string profile = Path.Combine(directory, "WebView2");
            File.WriteAllText(settings, "PRESERVE_SETTINGS");
            File.WriteAllText(backup, "PRESERVE_RECOVERY");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "fixture"), "PRESERVE_PROFILE");

            var clock = new Clock();
            DateTimeOffset start = clock.Now;
            var store = new CrashReportStore(path, clock);
            using var log = new StringWriter();
            using var listener = new TextWriterTraceListener(log);
            Trace.Listeners.Add(listener);
            try
            {
                Check(store.Maintain() && !File.Exists(path), "Missing crash report is a silent no-op.");
                string absent = Path.Combine(directory, "absent", "crash.log");
                Check(new CrashReportStore(absent, clock).Maintain() && !Directory.Exists(Path.GetDirectoryName(absent)),
                    "Maintenance does not create missing directories.");

                Check(store.Write(Capture()), "Sanitized exception report can be written.");
                string original = File.ReadAllText(path);
                Check(!original.Contains("DO_NOT_LOG") && !original.Contains("PRIVATE_DATA") &&
                    !original.Contains("private@example") && !original.Contains(@"C:\\Users") &&
                    !original.Contains(".cs:") && !original.Contains("HelpLink"),
                    "Messages, data, help links, paths and stack-trace text are omitted.");
                using (JsonDocument document = JsonDocument.Parse(original))
                {
                    JsonElement report = document.RootElement;
                    Check(report.GetProperty("SchemaVersion").GetInt32() == 1 &&
                        report.GetProperty("TimestampUtc").GetDateTimeOffset() == start &&
                        report.GetProperty("AppVersion").GetString()!.Length > 0, "Version/time metadata retained.");
                    JsonElement cause = report.GetProperty("Exceptions")[0];
                    Check(cause.GetProperty("Type").GetString()!.EndsWith("PrivateException") &&
                        cause.GetProperty("HResult").GetInt32() == unchecked((int)0x80070005), "Useful error type and HRESULT retained.");
                    Check(cause.GetProperty("CallSites").GetArrayLength() == CrashReportStore.MaximumCallSites &&
                        cause.GetProperty("CallSites").EnumerateArray().Any(site => site.GetString()!.Contains("ThrowPrivate")) &&
                        cause.GetProperty("Truncated").GetBoolean() && report.GetProperty("Truncated").GetBoolean(),
                        "Actual deep stack is capped and explicitly marked truncated.");
                }
                Check(store.Maintain() && File.ReadAllText(path) == original, "Fresh report is preserved byte-for-byte.");
                Check(store.Write(new ArgumentException("DO_NOT_LOG")), "A subsequent report replaces the first.");
                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
                    Check(document.RootElement.GetProperty("Exceptions").GetArrayLength() == 1 &&
                        document.RootElement.GetProperty("Exceptions")[0].GetProperty("Type").GetString() == "System.ArgumentException",
                        "Only the newest exception is retained.");

                Exception chain = Capture();
                for (int i = 0; i < 100; i++) chain = new Exception("DO_NOT_LOG", chain);
                Check(store.Write(chain), "Deep cause chains are accepted without recursive formatting.");
                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
                    Check(document.RootElement.GetProperty("Exceptions").GetArrayLength() == CrashReportStore.MaximumCauses &&
                        document.RootElement.GetProperty("Truncated").GetBoolean(), "Cause count is bounded and flagged.");
                Check(store.Write(new GenericException<Dictionary<string, List<Dictionary<string, List<string>>>>>()),
                    "Long generic type names are accepted.");
                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
                    Check(document.RootElement.GetProperty("Exceptions")[0].GetProperty("Type").GetString()!.Length ==
                        CrashReportStore.MaximumNameCharacters && document.RootElement.GetProperty("Truncated").GetBoolean(),
                        "Long names are bounded and flagged.");

                for (int i = 0; i < 100; i++) Check(store.Write(new Exception(new string('x', 100_000))), "Repeated crashes remain writable.");
                Check(new FileInfo(path).Length <= CrashReportStore.MaximumReportBytes &&
                    Directory.GetFiles(directory).Length == 3, "Repeated writes cannot grow a history, backup, or temp-file collection.");

                File.WriteAllText(path, original);
                clock.Now = start + CrashReportStore.Retention - TimeSpan.FromTicks(1);
                Check(store.Maintain() && File.Exists(path), "A report just younger than the retention limit survives.");
                clock.Now += TimeSpan.FromTicks(1);
                Check(store.Maintain() && !File.Exists(path), "A report expires at the exact retention boundary.");
                clock.Now = start - TimeSpan.FromTicks(1);
                File.WriteAllText(path, original);
                Check(store.Maintain() && !File.Exists(path), "Future timestamps do not bypass retention.");
                clock.Now = start;

                string exact = original + new string(' ', CrashReportStore.MaximumReportBytes - System.Text.Encoding.UTF8.GetByteCount(original));
                File.WriteAllText(path, exact);
                Check(new FileInfo(path).Length == CrashReportStore.MaximumReportBytes &&
                    store.Maintain() && File.Exists(path), "Valid report at the exact byte cap is retained.");
                File.WriteAllText(path, exact + " ");
                Check(store.Maintain() && !File.Exists(path), "One byte beyond the cap is discarded.");

                var unicode = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
                unicode["Exceptions"]![0]!["Type"] = new string('\u00e9', CrashReportStore.MaximumNameCharacters);
                string multibyte = unicode.ToJsonString(new JsonSerializerOptions {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                multibyte += new string(' ', CrashReportStore.MaximumReportBytes - System.Text.Encoding.UTF8.GetByteCount(multibyte));
                File.WriteAllText(path, multibyte);
                Check(multibyte.Length < CrashReportStore.MaximumReportBytes && store.Maintain() && File.Exists(path),
                    "Byte cap is not confused with character count.");
                File.WriteAllText(path, multibyte + " ");
                Check(store.Maintain() && !File.Exists(path), "Oversized multibyte report is discarded.");

                foreach (string invalid in new[] {
                    "", "legacy full exception: DO_NOT_LOG", "{", "null", "[]", "true", "\"text\"", "{}",
                    original.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2"),
                    original.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":\"1\""),
                    original.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":999999999999999999999999"),
                    original.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"Message\":\"DO_NOT_LOG\""),
                    original.Replace("PrivateException", @"\uD800"),
                    original.Replace("PrivateException", @"\uDC00"),
                    original.Replace("TimestampUtc", @"Timestamp\uD800"),
                    original.Replace("2026-10-13", @"\uD800"),
                    new string('[', 70) + new string(']', 70)
                })
                {
                    File.WriteAllText(path, invalid);
                    Check(store.Maintain() && !File.Exists(path), "Legacy, unknown or malformed report is removed.");
                }
                var mutations = new Action<System.Text.Json.Nodes.JsonObject>[] {
                    root => root["TimestampUtc"] = "invalid",
                    root => root["TimestampUtc"] = 1,
                    root => root["AppVersion"] = null,
                    root => root["AppVersion"] = new string('x', 65),
                    root => root["Truncated"] = "false",
                    root => root["Exceptions"] = new System.Text.Json.Nodes.JsonArray(),
                    root => root["Exceptions"] = "invalid",
                    root => root["Exceptions"] = new System.Text.Json.Nodes.JsonArray(null, null, null, null, null),
                    root => root["Exceptions"]![0] = null,
                    root => root["Exceptions"]![0]!["Type"] = "",
                    root => root["Exceptions"]![0]!["Type"] = new string('x', 129),
                    root => root["Exceptions"]![0]!["HResult"] = "invalid",
                    root => root["Exceptions"]![0]!["HResult"] = long.MaxValue,
                    root => root["Exceptions"]![0]!["Truncated"] = null,
                    root => root["Exceptions"]![0]!["Message"] = "DO_NOT_LOG",
                    root => root["Exceptions"]![0]!["CallSites"] = "invalid",
                    root => root["Exceptions"]![0]!["CallSites"]![0] = 123,
                    root => root["Exceptions"]![0]!["CallSites"]![0] = new string('x', 129),
                    root => root["Exceptions"]![0]!["CallSites"]!.AsArray().Add("extra"),
                };
                foreach (var mutate in mutations)
                {
                    var root = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
                    mutate(root);
                    File.WriteAllText(path, root.ToJsonString());
                    Check(store.Maintain() && !File.Exists(path), "Unexpected report shape is removed without escaping exceptions.");
                }

                File.WriteAllText(path, original);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Check(!store.Maintain(), "Inaccessible report maintenance is an explicit failure.");
                    Check(!store.Write(new Exception("DO_NOT_LOG")), "Locked report write is an explicit failure.");
                }
                Check(File.ReadAllText(path) == original, "A sharing violation does not truncate the prior report.");
                Check(store.Write(new Exception("DO_NOT_LOG")) && store.Maintain(), "Storage can recover on retry.");
                var invalidDestination = new CrashReportStore(profile, clock);
                Check(!invalidDestination.Write(new Exception("DO_NOT_LOG")) && !invalidDestination.Maintain(),
                    "A directory is not silently treated as a writable or missing report.");
                Trace.Flush();
                string trace = log.ToString();
                Check(trace.Contains("Could not write crash report") && trace.Contains("Could not maintain crash report") &&
                    !trace.Contains("PRIVATE_PATH") && !trace.Contains("DO_NOT_LOG") && !trace.Contains(directory),
                    "Storage failures are diagnosed without exposing exception details or paths.");
                Check(File.ReadAllText(settings) == "PRESERVE_SETTINGS" && File.ReadAllText(backup) == "PRESERVE_RECOVERY" &&
                    File.ReadAllText(Path.Combine(profile, "fixture")) == "PRESERVE_PROFILE",
                    "Maintenance never changes settings, recovery copies, or browser-profile data.");
                return _passed;
            }
            finally { Trace.Listeners.Remove(listener); }
        }
    }
}
'@
$usings = "using System;`nusing System.IO;`nusing System.Linq;`nusing System.Collections.Generic;`n"
Add-Type -TypeDefinition ($usings + $source + $tests) -CompilerOptions @('/nullable:enable', '/define:TRACE')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("SensoryStudio-CrashReports-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $count = [SensoryStudio.WinUI.Services.CrashReportTests]::Run($fixture)
    Write-Host "Passed $count crash-report checks plus startup/trace wiring checks (isolated fixtures; no real app data or app launch)."
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
