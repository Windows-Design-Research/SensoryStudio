using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Web.WebView2.Core;

namespace SensoryStudio.WinUI.Services;

internal sealed class LocalWebViewPolicy : IDisposable
{
    internal const string GardenOrigin = "https://zen-garden.local";
    internal const string GardenDocument = GardenOrigin + "/zen-garden.html";
    private readonly CoreWebView2 _core;
    private readonly bool _garden;
    private bool _disposed;

    public LocalWebViewPolicy(CoreWebView2 core, bool garden)
    {
        _core = core;
        _garden = garden;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsWebMessageEnabled = garden;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        core.NavigationStarting += NavigationStarting;
        core.FrameNavigationStarting += FrameNavigationStarting;
        core.NewWindowRequested += NewWindowRequested;
        core.PermissionRequested += PermissionRequested;
        core.DownloadStarting += DownloadStarting;
        core.WebResourceRequested += WebResourceRequested;
    }

    internal static bool IsGardenDocument(string source) =>
        IsGardenOrigin(source, out Uri? uri) &&
        uri.AbsolutePath == "/zen-garden.html" &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;

    internal static bool IsAllowedResource(string source, bool garden)
    {
        if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return true;
        if (source.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return garden
                ? IsGardenOrigin(source[5..], out _)
                : source.StartsWith("blob:null/", StringComparison.Ordinal);
        return garden ? IsGardenOrigin(source, out _) : source == "about:blank";
    }

    private static bool IsGardenOrigin(string source, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(source, UriKind.Absolute, out uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host == "zen-garden.local" &&
        uri.Port == 443 && uri.UserInfo.Length == 0;

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_garden ? IsGardenDocument(args.Uri) : args.Uri == "about:blank")
            return;
        args.Cancel = true;
        ReportBlocked("navigation");
    }

    private void FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        args.Cancel = true;
        ReportBlocked("frame navigation");
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        ReportBlocked("new window");
    }

    private void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.SavesInProfile = false;
        args.Handled = true;
        ReportBlocked("permission");
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
        ReportBlocked("download");
    }

    private void WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (IsAllowedResource(args.Request.Uri, _garden))
            return;
        args.Response = _core.Environment.CreateWebResourceResponse(
            null, 403, "Forbidden", "Content-Type: text/plain");
        ReportBlocked("non-local resource");
    }

    private static void ReportBlocked(string operation) =>
        Trace.TraceWarning($"Blocked unexpected WebView {operation}.");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _core.NavigationStarting -= NavigationStarting;
        _core.FrameNavigationStarting -= FrameNavigationStarting;
        _core.NewWindowRequested -= NewWindowRequested;
        _core.PermissionRequested -= PermissionRequested;
        _core.DownloadStarting -= DownloadStarting;
        _core.WebResourceRequested -= WebResourceRequested;
        _core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
    }
}
