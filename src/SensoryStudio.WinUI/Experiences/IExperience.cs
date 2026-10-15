using Microsoft.UI.Xaml.Controls;

namespace SensoryStudio.WinUI.Experiences;

public interface IExperience
{
    string Title { get; }
    UserControl View { get; }
    void Activate();
    void Deactivate();
}

public interface IPausableExperience
{
    bool IsPaused { get; }
    void TogglePause();
}

public interface IAudioExperience
{
    // Apply effective volume without starting playback or changing experience pause state.
    void SetVolume(double volume);
}

public interface IGardenSettings
{
    void SetParameter(string key, double value);
    void RunAction(string action);
}
