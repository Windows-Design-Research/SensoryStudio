#Requires -Version 7.4

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
$app = Join-Path $root 'src\SensoryStudio.WinUI'
$usings = [System.Collections.Generic.HashSet[string]]::new()
foreach ($using in @('using System;', 'using System.IO;', 'using System.Linq;', 'using System.Collections.Generic;', 'using SensoryStudio.WinUI.Services;')) {
    [void]$usings.Add($using)
}
$parts = foreach ($file in @('Services\LocalWebViewPolicy.cs', 'Services\GardenMessage.cs')) {
    $source = Get-Content (Join-Path $app $file) -Raw
    foreach ($match in [regex]::Matches($source, '(?m)^using .+;\r?$')) {
        [void]$usings.Add($match.Value.Trim())
    }
    $source = [regex]::Replace($source, '(?m)^using .+;\r?\n', '')
    [regex]::Replace($source, 'namespace ([\w.]+);', 'namespace $1 {') + "`n}"
}
$garden = Get-Content (Join-Path $app 'Experiences\ZenGardenExperience.xaml.cs') -Raw
$handler = [regex]::Match($garden, '(?ms)^    private void Browser_WebMessageReceived\(.*?^    \}')
if (!$handler.Success) { throw 'Garden message handler not found.' }
$bootstrap = [regex]::Match($garden, '(?s)            string rakeCursorVariables = .*?(?=            await Browser.CoreWebView2.AddScript)')
$savedLayout = [regex]::Match($garden, '(?ms)^    private static string GetSavedPlantLayoutScriptValue\(\).*?^    \}')
if (!$bootstrap.Success -or !$savedLayout.Success) { throw 'Garden bootstrap methods not found.' }
$defaultLayout = Get-Content (Join-Path $root 'Assets\ZenGarden\plant-layout-default.js') -Raw
$defaultLayout = [regex]::Replace($defaultLayout, '(?s)^.*?window.__defaultPlantLayout\s*=\s*', '').Trim().TrimEnd(';')
foreach ($pattern in @(
    'new LocalWebViewPolicy\(Browser.CoreWebView2, garden: true\)',
    'CoreWebView2HostResourceAccessKind.Deny\)',
    'Browser.CoreWebView2.Navigate\(LocalWebViewPolicy.GardenDocument\)',
    'window.top===window&&location.href===',
    'LocalWebViewPolicy.IsGardenDocument\(core.Source\)',
    'core.WebMessageReceived -= Browser_WebMessageReceived',
    '_webViewPolicy\?\.Dispose\(\)'
)) {
    if ($garden -notmatch $pattern) { throw "Missing garden boundary wiring: $pattern" }
}
foreach ($file in @('Assets\ZenGarden\zen-garden.html')) {
    $html = Get-Content (Join-Path $root $file) -Raw
    $csp = [regex]::Match($html, '<meta http-equiv="Content-Security-Policy" content="([^"]+)">')
    if (!$csp.Success -or $csp.Index -gt $html.IndexOf('<style>')) { throw "$file needs an early CSP." }
    $directives = $csp.Groups[1].Value.Split(';').Trim()
    foreach ($required in @("default-src 'none'", "connect-src 'none'", "frame-src 'none'", "worker-src 'none'", "object-src 'none'", "base-uri 'none'", "form-action 'none'")) {
        if ($required -notin $directives) { throw "$file lacks $required." }
    }
    if ($csp.Value -match 'unsafe-eval|https?:|\*') { throw "$file must not enable arbitrary network or eval sources." }
}

# Compile the real policy, parser and host handler against recording stubs; no browser or device opens.
$tests = @'
namespace Microsoft.Web.WebView2.Core
{
    public enum CoreWebView2WebResourceContext { All }
    public enum CoreWebView2WebResourceRequestSourceKinds { All }
    public enum CoreWebView2PermissionState { Default, Allow, Deny }
    public class CoreWebView2Settings
    {
        public bool AreDevToolsEnabled { get; set; } = true;
        public bool AreDefaultContextMenusEnabled { get; set; } = true;
        public bool AreHostObjectsAllowed { get; set; } = true;
        public bool AreDefaultScriptDialogsEnabled { get; set; } = true;
        public bool IsWebMessageEnabled { get; set; } = true;
    }
    public class CoreWebView2NavigationStartingEventArgs : EventArgs
    {
        public string Uri { get; set; } = "";
        public bool Cancel { get; set; }
    }
    public class CoreWebView2NewWindowRequestedEventArgs : EventArgs { public bool Handled { get; set; } }
    public class CoreWebView2PermissionRequestedEventArgs : EventArgs
    {
        public bool Handled { get; set; }
        public bool SavesInProfile { get; set; } = true;
        public CoreWebView2PermissionState State { get; set; }
    }
    public class CoreWebView2DownloadStartingEventArgs : EventArgs
    {
        public bool Handled { get; set; }
        public bool Cancel { get; set; }
    }
    public class CoreWebView2WebResourceRequest { public string Uri { get; set; } = ""; }
    public class CoreWebView2WebResourceResponse { public int StatusCode { get; set; } }
    public class CoreWebView2WebResourceRequestedEventArgs : EventArgs
    {
        public CoreWebView2WebResourceRequest Request { get; } = new();
        public CoreWebView2WebResourceResponse? Response { get; set; }
    }
    public class CoreWebView2WebMessageReceivedEventArgs : EventArgs
    {
        public string Source { get; set; } = "";
        public string WebMessageAsJson { get; set; } = "";
    }
    public class CoreWebView2Environment
    {
        public CoreWebView2WebResourceResponse CreateWebResourceResponse(Stream? stream, int status, string reason, string headers)
            => new() { StatusCode = status };
    }
    public class CoreWebView2
    {
        public CoreWebView2Settings Settings { get; } = new();
        public CoreWebView2Environment Environment { get; } = new();
        public int Filters { get; private set; }
        public int? LastResponseStatus { get; private set; }
        public event EventHandler<CoreWebView2NavigationStartingEventArgs>? NavigationStarting;
        public event EventHandler<CoreWebView2NavigationStartingEventArgs>? FrameNavigationStarting;
        public event EventHandler<CoreWebView2NewWindowRequestedEventArgs>? NewWindowRequested;
        public event EventHandler<CoreWebView2PermissionRequestedEventArgs>? PermissionRequested;
        public event EventHandler<CoreWebView2DownloadStartingEventArgs>? DownloadStarting;
        public event EventHandler<CoreWebView2WebResourceRequestedEventArgs>? WebResourceRequested;
        public void AddWebResourceRequestedFilter(string pattern, CoreWebView2WebResourceContext context, CoreWebView2WebResourceRequestSourceKinds kinds)
        {
            if (pattern != "*" || context != CoreWebView2WebResourceContext.All || kinds != CoreWebView2WebResourceRequestSourceKinds.All)
                throw new Exception("Incomplete resource filter.");
            Filters++;
        }
        public void RemoveWebResourceRequestedFilter(string pattern, CoreWebView2WebResourceContext context, CoreWebView2WebResourceRequestSourceKinds kinds) => Filters--;
        public bool Navigate(string uri, bool frame = false)
        {
            var args = new CoreWebView2NavigationStartingEventArgs { Uri = uri };
            if (frame) FrameNavigationStarting?.Invoke(this, args); else NavigationStarting?.Invoke(this, args);
            return !args.Cancel;
        }
        public bool OpenWindow()
        {
            var args = new CoreWebView2NewWindowRequestedEventArgs();
            NewWindowRequested?.Invoke(this, args);
            return !args.Handled;
        }
        public CoreWebView2PermissionRequestedEventArgs Permission()
        {
            var args = new CoreWebView2PermissionRequestedEventArgs();
            PermissionRequested?.Invoke(this, args);
            return args;
        }
        public CoreWebView2DownloadStartingEventArgs Download()
        {
            var args = new CoreWebView2DownloadStartingEventArgs();
            DownloadStarting?.Invoke(this, args);
            return args;
        }
        public bool Resource(string uri)
        {
            var args = new CoreWebView2WebResourceRequestedEventArgs();
            args.Request.Uri = uri;
            WebResourceRequested?.Invoke(this, args);
            LastResponseStatus = args.Response?.StatusCode;
            return args.Response is null;
        }
    }
}
namespace SensoryStudio.WinUI.Services
{
    internal static class AppSettingsService
    {
        internal sealed class Settings
        {
            public string? ZenGardenPlantLayout { get; set; }
            public bool HapticsEnabled { get; set; } = true;
        }
        internal static Settings Current { get; } = new();
        internal static int Saves;
        internal static bool Save() { Saves++; return true; }
    }
    internal enum HapticsType { Hover, Collide, Align, Grow, Error, Success }
    internal sealed class HapticsRecorder
    {
        internal readonly List<(HapticsType Type, double Intensity, bool Textured)> Calls = new();
        internal int Stops;
        internal void Stop() => Stops++;
        internal void Send(HapticsType type, double intensity = 1) => Calls.Add((type, intensity, false));
        internal void SendTextured(HapticsType type, double intensity) => Calls.Add((type, intensity, true));
    }
    internal static class ExperienceFeedback { internal static HapticsRecorder Haptics { get; } = new(); }
}
namespace SensoryStudio.WebViewTests
{
    internal sealed class Bridge
    {
        private bool _isPlantEditorActive;
        internal bool EditorActive => _isPlantEditorActive;
        internal int RestoredCursors;
        private void RestoreSystemCursor() => RestoredCursors++;
        public int RockInventory { get; private set; } = 3;
        public event EventHandler<int>? RockInventoryChanged;
        public event EventHandler<bool>? RockDropProximityChanged;
        __HANDLER__
        internal void Receive(string wire, string source = LocalWebViewPolicy.GardenDocument) =>
            Browser_WebMessageReceived(new CoreWebView2(), new CoreWebView2WebMessageReceivedEventArgs { Source = source, WebMessageAsJson = wire });
    }
    public static class Checks
    {
        __SAVED_LAYOUT__
        public static string Bootstrap()
        {
            const string gardenBaseUrl = LocalWebViewPolicy.GardenOrigin;
            const bool UseLegacyPlants = false, UseFantasyGrass = true;
            double _volume = 0.6;
            AppSettingsService.Current.ZenGardenPlantLayout = "{\"plants\":[],\"__proto__\":{\"marker\":true},\"label\":\"</script>\"}";
            __BOOTSTRAP__
            return bootstrapScript;
        }
        private static int _count;
        private static void Check(bool condition, string message)
        {
            _count++;
            if (!condition) throw new Exception(message);
        }
        private static string Wire(string payload) => JsonSerializer.Serialize(payload);
        private static GardenMessage Parse(string payload)
        {
            Check(GardenMessage.TryParse(LocalWebViewPolicy.GardenDocument, Wire(payload), out var message, out string error), "Valid message rejected: " + error);
            return message!;
        }
        public static int Run(string defaultLayout)
        {
            using var log = new StringWriter();
            using var listener = new TextWriterTraceListener(log);
            Trace.Listeners.Add(listener);
            const string page = LocalWebViewPolicy.GardenDocument;
            string[] rejectedDocuments = {
                "", "not a URI", "/zen-garden.html", "about:blank", "file:///C:/zen-garden.html",
                "https://example.invalid/zen-garden.html", "http://zen-garden.local/zen-garden.html",
                "https://zen-garden.local.evil.invalid/zen-garden.html", "https://zen-garden.local:444/zen-garden.html",
                "https://user@zen-garden.local/zen-garden.html", "https://zen-garden.local/other.html",
                page + "?x=1", page + "#fragment", "javascript:void(0)", "data:text/html,hello"
            };
            Check(LocalWebViewPolicy.IsGardenDocument(page), "Garden document allowed.");
            Check(LocalWebViewPolicy.IsGardenDocument("https://ZEN-GARDEN.local:443/zen-garden.html"), "Canonical origin accepted.");
            foreach (string source in rejectedDocuments)
            {
                Check(!LocalWebViewPolicy.IsGardenDocument(source), "Unexpected document trusted: " + source);
                Check(!GardenMessage.TryParse(source, Wire("{\"type\":\"plant-layout\",\"layout\":{}}"), out var rejected, out string error) &&
                    rejected is null && error.Length > 0, "Untrusted bridge source rejected.");
            }
            foreach (bool garden in new[] { true, false })
            {
                var core = new CoreWebView2();
                var policy = new LocalWebViewPolicy(core, garden);
                Check(!core.Settings.AreDevToolsEnabled && !core.Settings.AreDefaultContextMenusEnabled &&
                    !core.Settings.AreHostObjectsAllowed && !core.Settings.AreDefaultScriptDialogsEnabled &&
                    core.Settings.IsWebMessageEnabled == garden, "Least-privilege browser settings.");
                Check(core.Filters == 1, "All resource contexts and initiators filtered.");
                Check(core.Navigate(garden ? page : "about:blank"), "Normal document navigation retained.");
                Check(!core.Navigate(garden ? "about:blank" : page), "Other host document blocked.");
                Check(!core.Navigate(page, frame: true), "Even local subframes blocked.");
                Check(!core.OpenWindow(), "Popups blocked.");
                var permission = core.Permission();
                Check(permission.Handled && !permission.SavesInProfile && permission.State == CoreWebView2PermissionState.Deny, "Permissions denied without persistence.");
                var download = core.Download();
                Check(download.Handled && download.Cancel, "Downloads blocked.");
                foreach (string uri in new[] { "https://example.invalid/image.png", "http://zen-garden.local/a.png", "file:///C:/secret.txt", "data:text/html,hello", "blob:https://example.invalid/id" })
                {
                    Check(!core.Navigate(uri), "Untrusted navigation cancelled.");
                    Check(!core.Resource(uri) && core.LastResponseStatus == 403, "Non-local resource receives a denial, not success.");
                }
                Check(core.Resource("data:image/png;base64,AAAA"), "Generated canvas images allowed.");
                Check(core.Resource(garden ? "blob:https://zen-garden.local/id" : "blob:null/id"), "Local blob allowed.");
                Check(core.Resource("https://zen-garden.local/Grass1.webp") == garden, "Only garden may request mapped assets.");
                Check(core.Resource("https://zen-garden.local/meadow-renderer.js") == garden, "Local garden script allowed.");
                policy.Dispose();
                policy.Dispose();
                Check(core.Filters == 0, "Filter removed exactly once.");
                Check(core.Navigate("https://example.invalid") && core.Navigate(page, frame: true) && core.OpenWindow() &&
                    core.Resource("https://example.invalid") && !core.Permission().Handled && !core.Download().Cancel,
                    "All policy handlers detached.");
                using var second = new LocalWebViewPolicy(core, garden);
                Check(core.Filters == 1 && !core.Navigate("https://example.invalid"), "Retry attaches one effective policy.");
            }

            Check(Parse("{\"type\":\"ready\",\"values\":{}}").Type == "ready", "Ready accepted.");
            Check(Parse("rake rawMoves=42 (84/s)").Type == "diagnostic", "Legacy rake diagnostics remain harmless.");
            Check(Parse("guide overlay=800x600").Type == "diagnostic", "Legacy overlay diagnostics remain harmless.");
            Check(Parse("{\"type\":\"plant-layout\",\"layout\":{\"plants\":[{\"x\":0.25}],\"extra\":42}}").Layout ==
                "{\"plants\":[{\"x\":0.25}],\"extra\":42}", "Layout object preserved exactly.");
            Check(Parse("{\"type\":\"plant-layout\",\"layout\":" + defaultLayout + "}").Layout == defaultLayout,
                "Bundled authored plant layout fits the wire limit and is preserved.");
            foreach (bool active in new[] { true, false })
            {
                foreach (string type in new[] { "plant-editor", "rock-drop-proximity" })
                    Check(Parse($"{{\"type\":\"{type}\",\"active\":{active.ToString().ToLowerInvariant()}}}").Active == active, "Boolean state preserved.");
            }
            foreach (int count in new[] { int.MinValue, -1, 0, 1, 2, 3, 4, int.MaxValue })
                Check(Parse($"{{\"type\":\"rock-inventory\",\"count\":{count}}}").Count == Math.Clamp(count, 0, 3), "Inventory clamp preserved.");
            foreach (double intensity in new[] { -1d, 0d, 0.42d, 1d, 99d })
                Check(Parse("{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":{\"intensity\":" +
                    JsonSerializer.Serialize(intensity) + "}}").Intensity == Math.Clamp(intensity, 0, 1), "Intensity clamp preserved.");
            Check(Parse("{\"type\":\"interaction\",\"interaction\":\"rake\"}").Intensity == 1, "Missing detail retains default intensity.");
            Check(Parse("{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":{\"distance\":4}}").Intensity == 1, "Other detail fields preserved as no-op.");

            var bridge = new Bridge();
            int inventoryEvents = 0, proximityEvents = 0;
            bool proximity = false;
            bridge.RockInventoryChanged += (_, _) => inventoryEvents++;
            bridge.RockDropProximityChanged += (_, active) => { proximity = active; proximityEvents++; };
            bridge.Receive(Wire("{\"type\":\"plant-layout\",\"layout\":{\"plants\":[]}}"));
            Check(AppSettingsService.Saves == 1 && AppSettingsService.Current.ZenGardenPlantLayout == "{\"plants\":[]}", "Host saves valid layout.");
            bridge.Receive(Wire("{\"type\":\"plant-editor\",\"active\":true}"));
            Check(bridge.EditorActive && bridge.RestoredCursors == 1 && ExperienceFeedback.Haptics.Stops == 1, "Editor stops haptics/restores cursor.");
            bridge.Receive(Wire("{\"type\":\"plant-editor\",\"active\":false}"));
            Check(!bridge.EditorActive && ExperienceFeedback.Haptics.Stops == 1, "Exiting editor does not emit extra haptics.");
            bridge.Receive(Wire("{\"type\":\"rock-inventory\",\"count\":2}"));
            bridge.Receive(Wire("{\"type\":\"rock-drop-proximity\",\"active\":true}"));
            Check(bridge.RockInventory == 2 && inventoryEvents == 1 && proximity && proximityEvents == 1, "Host inventory/proximity events retained.");
            var interactions = new[] {
                ("rake", HapticsType.Hover, 0.42, false), ("collision", HapticsType.Collide, 0.42, false),
                ("rock-aligned", HapticsType.Align, 1d, false), ("rock-picked-up", HapticsType.Hover, 1d, false),
                ("rock-drag-start", HapticsType.Grow, 1d, false), ("grass", HapticsType.Hover, 0.42, true),
                ("stack-failed", HapticsType.Error, 0.42, false), ("stack-complete", HapticsType.Success, 0.42, false)
            };
            foreach (var (interaction, waveform, intensity, textured) in interactions)
            {
                bridge.Receive(Wire($"{{\"type\":\"interaction\",\"interaction\":\"{interaction}\",\"detail\":{{\"intensity\":0.42}}}}"));
                Check(ExperienceFeedback.Haptics.Calls.Last() == (waveform, intensity, textured), "Unchanged haptic mapping: " + interaction);
            }
            int calls = ExperienceFeedback.Haptics.Calls.Count;
            var invalid = new List<string> { "", "null", "42", "true", "[]", "{}", "{\"type\":\"ready\"}" };
            foreach (string payload in new[] {
                "", "{", "null", "[]", "42", "{}", "{\"type\":null}", "{\"type\":12}", "{\"type\":{}}",
                "{\"type\":\"unknown\"}", "{\"type\":\"plant-layout\"}", "{\"type\":\"plant-layout\",\"layout\":[]}",
                "{\"type\":\"plant-layout\",\"layout\":null}", "{\"type\":\"plant-layout\",\"layout\":\"{}\"}",
                "{\"type\":\"plant-editor\",\"active\":\"true\"}", "{\"type\":\"rock-drop-proximity\",\"active\":0}",
                "{\"type\":\"rock-inventory\",\"count\":null}", "{\"type\":\"rock-inventory\",\"count\":\"3\"}",
                "{\"type\":\"rock-inventory\",\"count\":3.5}", "{\"type\":\"rock-inventory\",\"count\":2147483648}",
                "{\"type\":\"interaction\"}", "{\"type\":\"interaction\",\"interaction\":[]}",
                "{\"type\":\"interaction\",\"interaction\":\"step\"}", "{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":null}",
                "{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":[]}",
                "{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":{\"intensity\":\"1\"}}",
                "{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":{\"intensity\":{}}}",
                "{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":{\"intensity\":1e999}}",
                "{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":{\"intensity\":-1e999}}",
                "{\"type\":\"interaction\",\"interaction\":\"rake\",\"detail\":{\"intensity\":NaN}}",
                new string('[', 70) + new string(']', 70)
            }) invalid.Add(Wire(payload));
            string ready = Wire("{\"type\":\"ready\"}");
            string boundary = ready + new string(' ', GardenMessage.MaximumMessageBytes - ready.Length);
            Check(GardenMessage.TryParse(page, boundary, out _, out _), "Exact encoded-byte limit accepted.");
            invalid.Add(boundary + " ");
            string prefix = "\"{\\\"type\\\":\\\"ready\\\",\\\"label\\\":\\\"";
            string suffix = "\\\"}\"";
            string unicodeBoundary = prefix +
                new string('\u00e9', (GardenMessage.MaximumMessageBytes - prefix.Length - suffix.Length) / 2) + suffix;
            unicodeBoundary += new string(' ', GardenMessage.MaximumMessageBytes - Encoding.UTF8.GetByteCount(unicodeBoundary));
            Check(unicodeBoundary.Length < GardenMessage.MaximumMessageBytes &&
                Encoding.UTF8.GetByteCount(unicodeBoundary) == GardenMessage.MaximumMessageBytes &&
                GardenMessage.TryParse(page, unicodeBoundary, out _, out _), "Exact UTF-8 byte limit accepts valid multibyte JSON.");
            Check(!GardenMessage.TryParse(page, unicodeBoundary + " ", out _, out string sizeError) &&
                sizeError == "Message exceeds the 4 MiB limit.", "One byte over the UTF-8 limit is rejected for size, not shape.");
            invalid.Add(unicodeBoundary + " ");
            foreach (string wire in invalid)
            {
                Check(!GardenMessage.TryParse(page, wire, out var message, out string error) && message is null && error.Length > 0, "Malformed/oversized message rejected.");
                bridge.Receive(wire);
            }
            foreach (string source in rejectedDocuments)
                bridge.Receive(Wire("{\"type\":\"plant-layout\",\"layout\":{\"changed\":true}}"), source);
            bridge.Receive(Wire("{\"type\":\"ready\"}"));
            bridge.Receive(Wire("rake rawMoves=4"));
            bridge.Receive(Wire("{\"type\":\"private-test-value\"}"));
            Check(AppSettingsService.Saves == 1 && AppSettingsService.Current.ZenGardenPlantLayout == "{\"plants\":[]}" &&
                ExperienceFeedback.Haptics.Calls.Count == calls && ExperienceFeedback.Haptics.Stops == 1 &&
                !bridge.EditorActive && bridge.RestoredCursors == 1 && bridge.RockInventory == 2 && inventoryEvents == 1 && proximityEvents == 1,
                "Rejected, diagnostic and ready messages have no host side effects.");
            Trace.Flush();
            Check(log.ToString().Contains("Rejected garden message:") && log.ToString().Contains("Blocked unexpected WebView") &&
                !log.ToString().Contains("private-test-value") && !log.ToString().Contains("example.invalid"),
                "Rejections are diagnosed without logging message contents or URLs.");
            Trace.Listeners.Remove(listener);
            return _count;
        }
    }
}
'@
$tests = $tests.Replace('__HANDLER__', $handler.Value)
$tests = $tests.Replace('__BOOTSTRAP__', $bootstrap.Value).Replace('__SAVED_LAYOUT__', $savedLayout.Value)
Add-Type -TypeDefinition (($usings -join "`n") + "`n" + ($parts -join "`n") + "`n" + $tests) -CompilerOptions @('/nullable:enable', '/define:TRACE')
$count = [SensoryStudio.WebViewTests.Checks]::Run($defaultLayout)
Write-Host "Passed $count WebView policy, parser, and host-handler checks plus CSP/lifecycle wiring checks."
$javascriptChecks = @'
const assert = require('node:assert/strict');
const vm = require('node:vm');
const script = new vm.Script(Buffer.from(process.argv[1], 'base64').toString('utf8'));
const page = 'https://zen-garden.local/zen-garden.html';
for (const [href, isTop] of [[page, true], ['https://example.invalid', true], [page, false]]) {
    const window = {};
    window.top = isTop ? window : {};
    let domReady;
    const styles = [];
    const document = {
        addEventListener: (name, callback) => { assert.equal(name, 'DOMContentLoaded'); domReady = callback; },
        createElement: () => ({}),
        head: { appendChild: style => styles.push(style) }
    };
    script.runInNewContext({ window, location: { href }, document });
    if (href === page && isTop) {
        assert.equal(window.__soundVolume, 0.6);
        assert.equal(window.__hapticsEnabled, true);
        assert.equal(window.__gardenFeatures.fantasyGrass, true);
        assert.equal(window.__gardenFeatures.fantasyGrassHeightScale, 0.68);
        assert.equal(Object.hasOwn(window.__gardenAssets, 'gameReadyGrass'), false);
        assert.equal(window.__gardenAssets.legacyPlants.tinyFlowers.length, 8);
        assert.equal(window.__savedPlantLayout.label, '</script>');
        assert.equal(Object.hasOwn(window.__savedPlantLayout, '__proto__'), true);
        assert.equal(window.__savedPlantLayout.marker, undefined);
        domReady();
        assert.equal(styles.length, 1);
        assert.match(styles[0].textContent, /https:\/\/zen-garden\.local\/5\.png/);
    } else {
        assert.deepEqual(Object.keys(window), ['top']);
        assert.equal(domReady, undefined);
    }
}
console.log('Passed actual bootstrap JavaScript: trusted top document only; assets, volume, layout and cursor retained.');
'@
$encodedBootstrap = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([SensoryStudio.WebViewTests.Checks]::Bootstrap()))
& node -e $javascriptChecks $encodedBootstrap
if ($LASTEXITCODE -ne 0) { throw 'Garden bootstrap JavaScript checks failed.' }
