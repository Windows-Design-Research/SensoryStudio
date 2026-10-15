using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SensoryStudio.WinUI.Services;
using Windows.Graphics;

namespace SensoryStudio.WinUI.Experiences;

public sealed partial class ExperienceWindow : Window
{
    private readonly IExperience _experience;
    private readonly string _experienceId;
    private bool _updatingSoundToggle = true;

    public ExperienceWindow(IExperience experience, string experienceId)
    {
        _experience = experience;
        _experienceId = experienceId;
        InitializeComponent();

        Title = experience.Title;
        TitleText.Text = experience.Title;
        ExperiencePresenter.Content = experience.View;
        SoundSettings_Changed(null, EventArgs.Empty);
        SoundSettingsService.Changed += SoundSettings_Changed;
        Closed += (_, _) => SoundSettingsService.Changed -= SoundSettings_Changed;
        HapticsToggle.IsOn = AppSettingsService.Current.HapticsEnabled;
        PauseButton.Visibility = experience is IPausableExperience
            ? Visibility.Visible
            : Visibility.Collapsed;

        AppWindow appWindow = GetAppWindow();
        WindowPlacementService.RestoreOrApplyDefault(
            appWindow,
            experienceId,
            GetInitialSize(experience));
        appWindow.Closing += AppWindow_Closing;
        Activated += ExperienceWindow_Activated;
    }

    private static SizeInt32 GetInitialSize(IExperience experience) => experience switch
    {
        PowderOrbExperience => new SizeInt32(720, 560),
        ZenGardenExperience => new SizeInt32(900, 640),
        _ => new SizeInt32(720, 560),
    };

    private AppWindow GetAppWindow()
    {
        IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));
    }

    private void ExperienceWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            _experience.Activate();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        WindowPlacementService.Save(sender, _experienceId);
        _experience.Deactivate();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_experience is not IPausableExperience pausable)
            return;

        pausable.TogglePause();
        PauseIcon.Glyph = pausable.IsPaused ? "\uE768" : "\uE769";
    }

    private void SoundToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSoundToggle)
            return;
        SoundSettingsService.SetSoundEnabled(SoundToggle.IsOn);
        AppSettingsService.Save();
    }

    private void SoundSettings_Changed(object? sender, EventArgs e)
    {
        _updatingSoundToggle = true;
        SoundToggle.IsOn = AppSettingsService.Current.SoundEnabled;
        _updatingSoundToggle = false;
    }

    private void HapticsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ExperienceFeedback.Haptics.Enabled = HapticsToggle.IsOn;
        AppSettingsService.Current.HapticsEnabled = HapticsToggle.IsOn;
        if (_experience is ZenGardenExperience garden)
            garden.SetHapticsEnabled(HapticsToggle.IsOn);
        AppSettingsService.Save();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
