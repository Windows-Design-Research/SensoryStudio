using SensoryStudio.WinUI.Experiences;

namespace SensoryStudio.WinUI.Services;

public static class SoundSettingsService
{
    private static readonly HashSet<IAudioExperience> AudioOwners = [];

    public static event EventHandler? Changed;

    public static void Register(IAudioExperience owner)
    {
        AudioOwners.Add(owner);
        owner.SetVolume(AppSettingsService.Current.EffectiveSoundVolume);
    }

    public static void Unregister(IAudioExperience owner)
    {
        AudioOwners.Remove(owner);
    }

    public static void SetSoundEnabled(bool enabled)
    {
        if (AppSettingsService.Current.SoundEnabled == enabled)
            return;

        AppSettingsService.Current.SoundEnabled = enabled;
        foreach (IAudioExperience owner in AudioOwners)
            owner.SetVolume(AppSettingsService.Current.EffectiveSoundVolume);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
