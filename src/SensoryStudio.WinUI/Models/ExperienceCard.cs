using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SensoryStudio.WinUI.Models;

public enum ExperienceCategory
{
    Focus,
    TakeABreak,
}

public sealed class ExperienceCard : INotifyPropertyChanged
{
    private string _selectedDurationTag = "3";

    public ExperienceCard(
        string id,
        string displayName,
        ExperienceCategory category,
        string description,
        string bestFor,
        string feedbackTypes,
        string thumbnailFile)
    {
        Id = id;
        DisplayName = displayName;
        Category = category;
        Description = description;
        BestFor = bestFor;
        FeedbackTypes = feedbackTypes;
        ThumbnailFile = thumbnailFile;
    }

    public string Id { get; set; }
    public string DisplayName { get; set; }
    public ExperienceCategory Category { get; set; }
    public string Description { get; set; }
    public string BestFor { get; set; }
    public string FeedbackTypes { get; set; }
    public string ThumbnailFile { get; set; }
    public string SelectedDurationTag
    {
        get => _selectedDurationTag;
        set
        {
            if (_selectedDurationTag == value)
                return;

            _selectedDurationTag = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDurationMinutes));
        }
    }
    public int SelectedDurationMinutes =>
        int.TryParse(SelectedDurationTag, out int minutes) ? minutes : 3;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string CategoryLabel => Category switch
    {
        ExperienceCategory.Focus => "Focus",
        _ => "Take a break",
    };

    public string ThumbnailUri => $"ms-appx:///Assets/icons/{Uri.EscapeDataString(ThumbnailFile)}";
}

public static class ExperienceCatalog
{
    public static IReadOnlyList<ExperienceCard> All { get; } =
    [
        new("powderorb", "Power Orb", ExperienceCategory.Focus,
            "Glide, press, and squeeze a floating particle orb with textured haptics.",
            "Playful release, sensory grounding, winding down",
            "Pointer · Haptics · Visual",
            "orb thumbnail.png"),
        new("zengardenhaptic", "Zen Garden", ExperienceCategory.TakeABreak,
            "Rake textured sand, then align and stack rocks that settle or topple.",
            "Grounding, tactile focus, winding down",
            "Pointer · Haptics · Sound · Visual",
            "zengarden thumbnail.png"),
    ];
}
