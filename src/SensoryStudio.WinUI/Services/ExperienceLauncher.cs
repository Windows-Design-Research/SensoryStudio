using SensoryStudio.WinUI.Experiences;

namespace SensoryStudio.WinUI.Services;

public static class ExperienceLauncher
{
    public static IExperience Create(string experienceId)
    {
        return experienceId.ToLowerInvariant() switch
        {
            "powderorb" => new PowderOrbExperience(),
            "zengardenhaptic" or "zensandart" or "zengarden3d" => new ZenGardenExperience(),
            _ => throw new ArgumentOutOfRangeException(nameof(experienceId), experienceId, "Unknown experience."),
        };
    }
}
