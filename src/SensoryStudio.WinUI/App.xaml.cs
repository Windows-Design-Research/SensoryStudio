using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using SensoryStudio.WinUI.Experiences;
using SensoryStudio.WinUI.Services;

namespace SensoryStudio.WinUI;

public partial class App : Application
{
    private static readonly CrashReportStore CrashReports = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SensoryStudio",
        "SensoryStudio.WinUI.crash.log"));

    private Window? _window;

    public App()
    {
        string webViewDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SensoryStudio",
            "WebView2");
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewDataFolder);

        InitializeComponent();
        HighContrastAdjustment = ApplicationHighContrastAdjustment.None;
        UnhandledException += (_, args) =>
        {
            CrashReports.Write(args.Exception);
        };
        CrashReports.Maintain();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppSettingsService.NoticeChanged += (_, _) =>
        {
            if (AppSettingsService.Notice is { } notice)
                SettingsNoticeDialog.Show(notice);
        };
        AppSettingsService.Load();
        ExperienceFeedback.Initialize(DispatcherQueue.GetForCurrentThread());
        ExperienceFeedback.Haptics.Enabled = AppSettingsService.Current.HapticsEnabled;
        ExperienceFeedback.Haptics.Intensity = AppSettingsService.Current.HapticsIntensity;
        string[] arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (arguments is ["--experience", var experienceId])
        {
            _window = new ExperienceWindow(
                ExperienceLauncher.Create(experienceId),
                experienceId);
            _window.Activate();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
