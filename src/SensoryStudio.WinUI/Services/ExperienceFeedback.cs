using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace SensoryStudio.WinUI.Services;

public static class ExperienceFeedback
{
    public static HapticService Haptics { get; } = new();
    public static InteractiveSoundService Sounds { get; } = new();

    public static void Initialize(DispatcherQueue dispatcherQueue)
    {
        Haptics.Initialize(dispatcherQueue);
        Sounds.Initialize();
        ElementSoundPlayer.State = ElementSoundPlayerState.Off;
    }

    public static void Invoke(HapticsType waveform)
    {
        Haptics.Send(waveform);
    }
}
