using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using SensoryStudio.WinUI.Experiences;
using SensoryStudio.WinUI.Models;
using SensoryStudio.WinUI.Services;
using Windows.Graphics;

namespace SensoryStudio.WinUI;

public sealed partial class MainWindow : Window
{
    private ExperienceCategory? _activeFilter;
    private readonly NativeHotkeyService _hotkeys = new();
    private readonly SystemThemeSubscription _themeSubscription;
    private bool _hotkeysRegistered;
    private IExperience? _activeExperience;
    private string? _activeExperienceId;
    private CompactExperienceWindow? _compactExperienceWindow;
    private IExperience? _suspendedPopOutExperience;
    private bool _suspendedPopOutWasPaused;
    private readonly DispatcherQueueTimer _sessionTimer;
    private TimeSpan? _sessionDuration;
    private TimeSpan _sessionRemaining;
    private DateTimeOffset _lastSessionTick;
    private bool _fallbackPaused;
    private int _displayedRockInventory = 3;
    private bool _restoreWindowAfterExperience;
    private bool _updatingSoundToggle = true;

    public ObservableCollection<ExperienceCard> VisibleExperiences { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        SoundSettings_Changed(null, EventArgs.Empty);
        SoundSettingsService.Changed += SoundSettings_Changed;
        _themeSubscription = new SystemThemeSubscription(RootGrid);
        _sessionTimer = DispatcherQueue.CreateTimer();
        _sessionTimer.Interval = TimeSpan.FromMilliseconds(16);
        _sessionTimer.Tick += SessionTimer_Tick;
        ConfigureWindow();
        ApplyFilter();
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_hotkeysRegistered)
        {
            _hotkeysRegistered = true;
            IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _hotkeys.Triggered += Launch;
            _hotkeys.Register(handle);
        }

        if (args.WindowActivationState != WindowActivationState.Deactivated)
            _activeExperience?.Activate();
    }

    private void ConfigureWindow()
    {
        AppWindow appWindow = GetAppWindowForCurrentWindow();
        appWindow.Resize(new SizeInt32(1280, 800));
        if (appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "fidgetpad.ico"));
        appWindow.TitleBar.ExtendsContentIntoTitleBar = false;
    }

    private AppWindow GetAppWindowForCurrentWindow()
    {
        IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        return AppWindow.GetFromWindowId(windowId);
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string filter } || ExperienceGrid == null)
            return;

        _activeFilter = filter switch
        {
            "Focus" => ExperienceCategory.Focus,
            "TakeABreak" => ExperienceCategory.TakeABreak,
            _ => null,
        };
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        VisibleExperiences.Clear();
        IEnumerable<ExperienceCard> source = ExperienceCatalog.All.Where(card =>
            _activeFilter == null || card.Category == _activeFilter);
        foreach (ExperienceCard card in source)
        {
            VisibleExperiences.Add(card);
        }
    }

    private void ExperienceGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ExperienceCard card)
            Launch(card);
    }

    private void ExperienceCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.Scale = new Vector3(1.02f, 1.02f, 1);
            card.Translation = new Vector3(0, -2, 16);
        }
    }

    private void ExperienceCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.Scale = Vector3.One;
            card.Translation = new Vector3(0, 0, 8);
        }
    }

    private void Launch(ExperienceCard card)
    {
        Launch(card.Id, card.SelectedDurationMinutes);
    }

    private void Launch(string experienceId)
    {
        Launch(experienceId, null);
    }

    private void Launch(string experienceId, int? durationMinutes)
    {
        try
        {
            CloseExperience();
            if (durationMinutes is int minutes)
                SetSessionDurationMinutes(minutes);

            IExperience experience = ExperienceLauncher.Create(experienceId);
            _activeExperience = experience;
            _activeExperienceId = experienceId;
            ExperienceTitleText.Text = experience.Title;
            ExperiencePresenter.Content = experience.View;
            if (experience is ZenGardenExperience garden)
                garden.SetHostWindow(this, ExperienceControlsPill, GardenTools);
            ExperienceSoundToggle.IsOn = AppSettingsService.Current.SoundEnabled;
            ExperienceHapticsToggle.IsOn = AppSettingsService.Current.HapticsEnabled;
            ExperiencePauseButton.Visibility = Visibility.Visible;
            ConfigureExperienceSettings(experience);
            ExperiencePauseIcon.Glyph = "\uE769";
            _fallbackPaused = false;
            ResetSessionTimer();
            LaunchError.Visibility = Visibility.Collapsed;
            ExperienceHost.Visibility = Visibility.Visible;
            MaximizeForExperience();
            experience.Activate();
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException)
        {
            LaunchErrorText.Text = exception.Message;
            LaunchError.Visibility = Visibility.Visible;
        }
    }

    private void CloseExperience_Click(object sender, RoutedEventArgs e)
    {
        CloseExperience();
    }

    private void CloseExperience()
    {
        if (_activeExperience == null)
            return;

        _activeExperience.Deactivate();
        if (_activeExperience is ZenGardenExperience garden)
        {
            garden.RockInventoryChanged -= Garden_RockInventoryChanged;
            garden.RockDropProximityChanged -= Garden_RockDropProximityChanged;
        }
        _sessionTimer.Stop();
        ExperiencePresenter.Content = null;
        _activeExperience = null;
        _activeExperienceId = null;
        ExperienceHost.Visibility = Visibility.Collapsed;
        RestoreWindowAfterExperience();
        AppSettingsService.Save();
    }

    private void MaximizeForExperience()
    {
        if (GetAppWindowForCurrentWindow().Presenter is not OverlappedPresenter presenter)
            return;

        _restoreWindowAfterExperience = presenter.State != OverlappedPresenterState.Maximized;
        presenter.Maximize();
    }

    private void RestoreWindowAfterExperience()
    {
        if (!_restoreWindowAfterExperience)
            return;

        if (GetAppWindowForCurrentWindow().Presenter is OverlappedPresenter presenter)
            presenter.Restore();

        _restoreWindowAfterExperience = false;
    }

    private void ExperiencePauseButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleActiveExperiencePause();
    }

    internal void ToggleActiveExperiencePause()
    {
        if (_activeExperience == null)
            return;

        bool isPaused;
        if (_activeExperience is IPausableExperience pausable)
        {
            pausable.TogglePause();
            isPaused = pausable.IsPaused;
        }
        else
        {
            _fallbackPaused = !_fallbackPaused;
            if (_fallbackPaused)
                _activeExperience.Deactivate();
            else
                _activeExperience.Activate();
            isPaused = _fallbackPaused;
        }

        ExperiencePauseIcon.Glyph = isPaused ? "\uE768" : "\uE769";
        if (isPaused)
        {
            _sessionTimer.Stop();
        }
        else if (_sessionDuration != null && _sessionRemaining > TimeSpan.Zero)
        {
            _lastSessionTick = DateTimeOffset.Now;
            _sessionTimer.Start();
        }
    }

    internal bool IsActiveExperiencePaused =>
        _activeExperience is IPausableExperience pausable
            ? pausable.IsPaused
            : _fallbackPaused;

    internal double SessionProgress =>
        _sessionDuration == null
            ? 1
            : Math.Clamp(
                _sessionRemaining.TotalMilliseconds / _sessionDuration.Value.TotalMilliseconds,
                0,
                1);

    internal string SessionTimeAutomationName =>
        _sessionDuration == null
            ? "No session time limit"
            : $"Session time remaining: {Math.Max(0, (int)Math.Ceiling(_sessionRemaining.TotalSeconds)) / 60}:" +
              $"{Math.Max(0, (int)Math.Ceiling(_sessionRemaining.TotalSeconds)) % 60:00}";

    internal int SelectedDurationMinutes
    {
        get
        {
            return ExperienceDurationComboBox.Items
                    .OfType<ToggleMenuFlyoutItem>()
                    .FirstOrDefault(item => item.IsChecked)?.Tag is string tag &&
                int.TryParse(tag, out int minutes)
                    ? minutes : 3;
        }
    }

    internal void SetSessionDurationMinutes(int minutes)
    {
        foreach (ToggleMenuFlyoutItem item in ExperienceDurationComboBox.Items)
        {
            if (item.Tag is string tag &&
                int.TryParse(tag, out int itemMinutes) &&
                itemMinutes == minutes)
            {
                SelectDurationItem(item);
                break;
            }
        }
    }

    private void ExperienceTimerTrack_Click(object sender, RoutedEventArgs e)
    {
        ExperienceDurationComboBox.ShowAt(ExperienceTimerTrack);
    }

    private void ExperienceDurationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem item)
            SelectDurationItem(item);
    }

    private void SelectDurationItem(ToggleMenuFlyoutItem selectedItem)
    {
        foreach (ToggleMenuFlyoutItem item in ExperienceDurationComboBox.Items)
            item.IsChecked = ReferenceEquals(item, selectedItem);

        string? selectedTag = selectedItem.Tag as string;
        ExperienceDurationText.Text = FormatDurationLabel(selectedTag);
        if (_activeExperienceId != null && selectedTag != null)
        {
            ExperienceCard? card = ExperienceCatalog.All.FirstOrDefault(
                item => item.Id == _activeExperienceId);
            if (card != null)
                card.SelectedDurationTag = selectedTag;
        }
        if (_activeExperience != null)
            ResetSessionTimer();
    }

    private void ResetSessionTimer()
    {
        _sessionTimer.Stop();
        if (ExperienceDurationComboBox.Items
                .OfType<ToggleMenuFlyoutItem>()
                .FirstOrDefault(item => item.IsChecked)?.Tag is not string tag ||
            !int.TryParse(tag, out int minutes))
        {
            return;
        }

        if (minutes == 0)
        {
            _sessionDuration = null;
            _sessionRemaining = TimeSpan.Zero;
            ExperiencePillCountdownStroke.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(ExperienceTimerTrack, "No session time limit");
            return;
        }

        ExperiencePillCountdownStroke.Visibility = Visibility.Visible;
        _sessionDuration = TimeSpan.FromMinutes(minutes);
        _sessionRemaining = _sessionDuration.Value;
        _lastSessionTick = DateTimeOffset.Now;
        UpdateTimerRing(1);
        UpdateTimerAutomationName();
        _sessionTimer.Start();
    }

    private async void SessionTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_sessionDuration == null || _activeExperience == null)
            return;

        DateTimeOffset now = DateTimeOffset.Now;
        _sessionRemaining -= now - _lastSessionTick;
        _lastSessionTick = now;
        if (_sessionRemaining <= TimeSpan.Zero)
        {
            _sessionRemaining = TimeSpan.Zero;
            UpdateTimerRing(0);
            sender.Stop();
            PauseExperienceAtSessionEnd();
            await ShowSessionCompletionDialogAsync();
            CloseExperience();
        }
        else
        {
            double progress =
                _sessionRemaining.TotalMilliseconds / _sessionDuration.Value.TotalMilliseconds;
            UpdateTimerRing(progress);
        }

        UpdateTimerAutomationName();
    }

    private void UpdateTimerRing(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        UpdatePillCountdownStroke(ExperiencePillCountdownStroke, progress);
    }

    internal static void UpdatePillCountdownStroke(
        Microsoft.UI.Xaml.Shapes.Rectangle stroke,
        double progress)
    {
        stroke.Visibility = Visibility.Visible;
        double width = Math.Max(1, stroke.ActualWidth);
        double height = Math.Max(1, stroke.ActualHeight);
        double radius = Math.Min(27, Math.Min(width, height) / 2);
        double perimeter = (2 * (width + height - (4 * radius))) + (2 * Math.PI * radius);
        double units = perimeter / Math.Max(1, stroke.StrokeThickness);
        stroke.StrokeDashArray = new DoubleCollection
        {
            Math.Max(0.001, units * Math.Clamp(progress, 0, 1)),
            Math.Max(0.001, units),
        };
    }

    internal static string FormatDurationLabel(string? tag) =>
        tag == "0" ? "∞" : $"{tag ?? "3"}m";

    private void PauseExperienceAtSessionEnd()
    {
        if (_activeExperience is IPausableExperience pausable)
        {
            if (!pausable.IsPaused)
                pausable.TogglePause();
        }
        else if (!_fallbackPaused)
        {
            _fallbackPaused = true;
            _activeExperience?.Deactivate();
        }

        ExperiencePauseIcon.Glyph = "\uE768";
    }

    private async Task ShowSessionCompletionDialogAsync()
    {
        if (_compactExperienceWindow is { } compactWindow)
        {
            await compactWindow.ShowSessionCompletionDialogAsync();
            return;
        }

        if (RootGrid.XamlRoot is { } xamlRoot)
            await SessionCompletionDialog.ShowAsync(xamlRoot);
    }

    private void UpdateTimerAutomationName()
    {
        int remainingSeconds = Math.Max(0, (int)Math.Ceiling(_sessionRemaining.TotalSeconds));
        AutomationProperties.SetName(
            ExperienceTimerTrack,
            $"Session time remaining: {remainingSeconds / 60}:{remainingSeconds % 60:00}");
    }

    private void PopOutExperience_Click(object sender, RoutedEventArgs e)
    {
        if (_activeExperience == null ||
            _activeExperienceId == null ||
            _compactExperienceWindow != null)
            return;

        IExperience experience = _activeExperience;
        if (experience is ZenGardenExperience garden)
        {
            _suspendedPopOutExperience = garden;
            _suspendedPopOutWasPaused = garden.IsPaused;
            if (!garden.IsPaused)
                garden.TogglePause();

            experience = new ZenGardenExperience();
            _activeExperience = experience;
        }
        else
        {
            ExperiencePresenter.Content = null;
        }
        ExperienceHost.Visibility = Visibility.Collapsed;

        _compactExperienceWindow = new CompactExperienceWindow(
            experience,
            _activeExperienceId,
            this,
            RestoreCompactExperience);
        _compactExperienceWindow.Activate();
        if (_suspendedPopOutExperience != null)
            experience.Activate();
        GetAppWindowForCurrentWindow().Hide();
    }

    private void RestoreCompactExperience(IExperience experience)
    {
        _compactExperienceWindow = null;
        if (_suspendedPopOutExperience is IExperience suspendedExperience)
        {
            experience.Deactivate();
            _activeExperience = suspendedExperience;
            _suspendedPopOutExperience = null;
            if (!_suspendedPopOutWasPaused &&
                suspendedExperience is IPausableExperience { IsPaused: true } pausable)
            {
                pausable.TogglePause();
            }

            ExperienceHost.Visibility = Visibility.Visible;
            GetAppWindowForCurrentWindow().Show();
            Activate();
            return;
        }

        _activeExperience = experience;
        ExperiencePresenter.Content = experience.View;
        if (experience is ZenGardenExperience garden)
            garden.SetHostWindow(this, ExperienceControlsPill, GardenTools);
        ExperienceHost.Visibility = Visibility.Visible;
        GetAppWindowForCurrentWindow().Show();
        Activate();
    }

    internal void CloseActiveExperienceFromCompact()
    {
        CloseExperience();
    }

    private void ExperienceSoundToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSoundToggle)
            return;
        SoundSettingsService.SetSoundEnabled(ExperienceSoundToggle.IsOn);
        AppSettingsService.Save();
    }

    private void SoundSettings_Changed(object? sender, EventArgs e)
    {
        _updatingSoundToggle = true;
        ExperienceSoundToggle.IsOn = AppSettingsService.Current.SoundEnabled;
        _updatingSoundToggle = false;
    }

    private void ExperienceHapticsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        ExperienceFeedback.Haptics.Enabled = ExperienceHapticsToggle.IsOn;
        AppSettingsService.Current.HapticsEnabled = ExperienceHapticsToggle.IsOn;
        if (_activeExperience is ZenGardenExperience garden)
            garden.SetHapticsEnabled(ExperienceHapticsToggle.IsOn);
        AppSettingsService.Save();
    }

    private void ConfigureExperienceSettings(IExperience experience)
    {
        GardenTools.Visibility = experience is ZenGardenExperience
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (experience is ZenGardenExperience garden)
        {
            SetRockInventoryVisuals(garden.RockInventory);
            garden.RockInventoryChanged += Garden_RockInventoryChanged;
            garden.RockDropProximityChanged += Garden_RockDropProximityChanged;
        }
    }

    private void GardenSlider_ValueChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_activeExperience is IGardenSettings garden &&
            sender is Slider { Tag: string key })
        {
            garden.SetParameter(key, e.NewValue);
        }
    }

    private void GardenLines_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_activeExperience is IGardenSettings garden &&
            sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string value } } &&
            int.TryParse(value, out int lines))
        {
            garden.SetParameter("tines", lines);
            GardenRakeImage.Source = new BitmapImage(
                new Uri($"ms-appx:///Assets/ZenGarden/{lines}.png"));
        }
    }

    private void GardenAction_Click(object sender, RoutedEventArgs e)
    {
        if (_activeExperience is IGardenSettings garden &&
            sender is Button { Tag: string action })
        {
            garden.RunAction(action);
        }
    }

    private void RockStackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeExperience is not ZenGardenExperience garden)
            return;

        garden.RunAction("rocksButton");
    }

    private void AnimateRockStackJump(int rockCount, int departingIndex)
    {
        Storyboard animation = new();
        if (rockCount >= 1 && departingIndex != 0)
            AddWeightedRockJump(animation, RockBottomTranslation, -4, 80, 190, 400);
        if (rockCount >= 2 && departingIndex != 1)
            AddWeightedRockJump(animation, RockMiddleTranslation, -7, 45, 145, 340);
        if (rockCount >= 3 && departingIndex != 2)
            AddWeightedRockJump(animation, RockTopTranslation, -10, 20, 110, 280);

        Image departingImage = departingIndex switch
        {
            2 => RockTopImage,
            1 => RockMiddleImage,
            _ => RockBottomImage,
        };
        TranslateTransform departingTranslation = departingIndex switch
        {
            2 => RockTopTranslation,
            1 => RockMiddleTranslation,
            _ => RockBottomTranslation,
        };
        AddDepartingRock(animation, departingImage, departingTranslation);
        animation.Completed += (_, _) =>
        {
            departingImage.Opacity = 0;
            departingTranslation.Y = 0;
        };
        animation.Begin();
    }

    private static void AddWeightedRockJump(
        Storyboard animation,
        TranslateTransform target,
        double lift,
        int delayMilliseconds,
        int apexMilliseconds,
        int landingMilliseconds)
    {
        DoubleAnimationUsingKeyFrames jump = new();
        jump.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMilliseconds)),
            Value = 1,
            KeySpline = CreateKeySpline(0.4, 0, 0.8, 0.2),
        });
        jump.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(apexMilliseconds)),
            Value = lift,
            KeySpline = CreateKeySpline(0.2, 0.8, 0.2, 1),
        });
        jump.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(landingMilliseconds)),
            Value = 0,
            KeySpline = CreateKeySpline(0.4, 0, 0.2, 1),
        });
        Storyboard.SetTarget(jump, target);
        Storyboard.SetTargetProperty(jump, "Y");
        animation.Children.Add(jump);
    }

    private static KeySpline CreateKeySpline(
        double controlPoint1X,
        double controlPoint1Y,
        double controlPoint2X,
        double controlPoint2Y) =>
        new()
        {
            ControlPoint1 = new Windows.Foundation.Point(controlPoint1X, controlPoint1Y),
            ControlPoint2 = new Windows.Foundation.Point(controlPoint2X, controlPoint2Y),
        };

    private static void AddDepartingRock(
        Storyboard animation,
        Image image,
        TranslateTransform translation)
    {
        DoubleAnimationUsingKeyFrames movement = new();
        movement.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)),
            Value = 1,
            KeySpline = CreateKeySpline(0.4, 0, 0.8, 0.2),
        });
        movement.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(155)),
            Value = -9,
            KeySpline = CreateKeySpline(0.2, 0.8, 0.2, 1),
        });
        movement.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340)),
            Value = -18,
            KeySpline = CreateKeySpline(0.4, 0, 1, 1),
        });

        DoubleAnimation opacity = new()
        {
            From = 1,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(130),
            Duration = TimeSpan.FromMilliseconds(210),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(movement, translation);
        Storyboard.SetTargetProperty(movement, "Y");
        Storyboard.SetTarget(opacity, image);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        animation.Children.Add(movement);
        animation.Children.Add(opacity);
    }

    private void Garden_RockInventoryChanged(object? sender, int count)
    {
        int previousCount = _displayedRockInventory;
        _displayedRockInventory = count;
        if (Math.Abs(count - previousCount) != 1)
        {
            SetRockInventoryVisuals(count);
            return;
        }

        if (count < previousCount)
            AnimateRockStackJump(previousCount, previousCount - 1);
        else
            AnimateReturningRock(count - 1);
    }

    private void Garden_RockDropProximityChanged(object? sender, bool active)
    {
        string rockAction = active ? "Return rock" : "Add rock";
        ToolTipService.SetToolTip(RockStackButton, rockAction);
        AutomationProperties.SetName(RockStackButton, rockAction);

        DoubleAnimation animation = new()
        {
            To = active ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(active ? 140 : 220),
            EasingFunction = new QuadraticEase
            {
                EasingMode = active ? EasingMode.EaseOut : EasingMode.EaseIn,
            },
        };
        Storyboard.SetTarget(animation, RockDropHint);
        Storyboard.SetTargetProperty(animation, "Opacity");
        Storyboard storyboard = new();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void SetRockInventoryVisuals(int count)
    {
        _displayedRockInventory = count;
        RockBottomImage.Opacity = count >= 1 ? 1 : 0;
        RockMiddleImage.Opacity = count >= 2 ? 1 : 0;
        RockTopImage.Opacity = count >= 3 ? 1 : 0;
        RockBottomTranslation.Y = 0;
        RockMiddleTranslation.Y = 0;
        RockTopTranslation.Y = 0;
    }

    private void AnimateReturningRock(int index)
    {
        Image image = index switch
        {
            2 => RockTopImage,
            1 => RockMiddleImage,
            _ => RockBottomImage,
        };
        TranslateTransform translation = index switch
        {
            2 => RockTopTranslation,
            1 => RockMiddleTranslation,
            _ => RockBottomTranslation,
        };

        image.Opacity = 0;
        translation.Y = -12;

        DoubleAnimation movement = new()
        {
            From = -12,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new BounceEase
            {
                Bounces = 2,
                Bounciness = 2,
                EasingMode = EasingMode.EaseOut,
            },
        };
        DoubleAnimation opacity = new()
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
        };
        Storyboard.SetTarget(movement, translation);
        Storyboard.SetTargetProperty(movement, "Y");
        Storyboard.SetTarget(opacity, image);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        Storyboard animation = new();
        animation.Children.Add(movement);
        animation.Children.Add(opacity);
        animation.Completed += (_, _) =>
        {
            image.Opacity = 1;
            translation.Y = 0;
        };
        animation.Begin();
    }

    private void RockStackButton_LayoutUpdated(object? sender, object e)
    {
        if (_activeExperience is ZenGardenExperience garden)
            garden.SetRockDropTarget(RockStackButton);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        SoundSettingsService.Changed -= SoundSettings_Changed;
        _themeSubscription.Dispose();
        _compactExperienceWindow?.Close();
        CloseExperience();
        _hotkeys.Dispose();
    }
}
