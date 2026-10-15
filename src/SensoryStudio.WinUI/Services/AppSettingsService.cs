using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SensoryStudio.WinUI.Services;

public sealed class AppSettings
{
    public bool SoundEnabled { get; set; } = true;
    public double SoundVolume { get; set; } = 0.6;
    public bool HapticsEnabled { get; set; } = true;
    public double HapticsIntensity { get; set; } = 1;
    public string? ZenGardenPlantLayout { get; set; }
    public Dictionary<string, WindowBounds> WindowBounds { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    [JsonIgnore]
    public double EffectiveSoundVolume => SoundEnabled ? SoundVolume : 0;
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    internal bool IsValid =>
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        Left >= int.MinValue && Left <= int.MaxValue &&
        Top >= int.MinValue && Top <= int.MaxValue &&
        Width >= 200 && Width <= int.MaxValue &&
        Height >= 200 && Height <= int.MaxValue &&
        Math.Round(Left) + Math.Round(Width) <= int.MaxValue &&
        Math.Round(Top) + Math.Round(Height) <= int.MaxValue;
}

public enum SettingsNoticeKind
{
    Recovered,
    ReadFailed,
    RecoveryFailed,
    SaveFailed,
}

public sealed record SettingsNotice(SettingsNoticeKind Kind, string Detail);

internal sealed class SettingsStore(string path)
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private bool _writesBlocked;

    public SettingsNotice? Notice { get; private set; }
    public event EventHandler? NoticeChanged;

    public AppSettings Load()
    {
        _writesBlocked = false;
        Notice = null;
        AppSettings settings;
        bool repaired;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes)
                throw new JsonException("The settings file exceeds the 4 MiB size limit.");
            using var reader = new StreamReader(stream);
            using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
            settings = ReadSettings(document.RootElement, out repaired);
        }
        catch (FileNotFoundException)
        {
            return new();
        }
        catch (DirectoryNotFoundException)
        {
            return new();
        }
        catch (JsonException)
        {
            settings = new();
            repaired = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _writesBlocked = true;
            Report(SettingsNoticeKind.ReadFailed, exception.Message);
            return new();
        }

        if (repaired)
        {
            string backup = path + $".recovery-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json";
            try
            {
                File.Copy(path, backup, overwrite: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _writesBlocked = true;
                Report(SettingsNoticeKind.RecoveryFailed, exception.Message);
                return settings;
            }
            Report(SettingsNoticeKind.Recovered, backup);
        }
        return settings;
    }

    public bool Save(AppSettings settings)
    {
        if (_writesBlocked)
            return false;

        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        bool temporaryCreated = false;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, WriteOptions);
            if (bytes.Length > MaximumFileBytes)
                throw new JsonException("The settings file exceeds the 4 MiB size limit.");
            using (JsonDocument document = JsonDocument.Parse(bytes))
            {
                ReadSettings(document.RootElement, out bool repaired);
                if (repaired)
                    throw new JsonException("Settings contain invalid values and were not saved.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaryCreated = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Replace only after the complete file has been flushed, on the same volume.
            if (File.Exists(path))
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, path);
            temporaryCreated = false;
            if (Notice?.Kind == SettingsNoticeKind.SaveFailed)
                Notice = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Report(SettingsNoticeKind.SaveFailed, exception.Message);
            return false;
        }
        finally
        {
            try
            {
                if (temporaryCreated)
                    File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Report(SettingsNoticeKind.SaveFailed,
                    $"Could not remove the temporary settings file '{temporaryPath}': {exception.Message}");
            }
        }
    }

    private void Report(SettingsNoticeKind kind, string detail)
    {
        var notice = new SettingsNotice(kind, detail);
        if (Notice == notice)
            return;
        Notice = notice;
        Trace.TraceWarning($"Settings notice: {kind}.");
        NoticeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static AppSettings ReadSettings(JsonElement root, out bool repaired)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Settings must be a JSON object.");
        var data = new JsonObject();
        foreach (JsonProperty property in root.EnumerateObject())
            data[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        repaired = false;
        repaired |= RemoveInvalid<bool>(data, nameof(AppSettings.SoundEnabled), _ => true);
        repaired |= RemoveInvalid<bool>(data, nameof(AppSettings.HapticsEnabled), _ => true);
        repaired |= RemoveInvalid<double>(data, nameof(AppSettings.SoundVolume), IsUnitValue);
        repaired |= RemoveInvalid<double>(data, nameof(AppSettings.HapticsIntensity), IsUnitValue);
        if (data[nameof(AppSettings.ZenGardenPlantLayout)] is not null)
            repaired |= RemoveInvalid<string>(data, nameof(AppSettings.ZenGardenPlantLayout), IsValidPlantLayout);

        if (data.TryGetPropertyValue(nameof(AppSettings.WindowBounds), out JsonNode? bounds))
        {
            if (bounds is not JsonObject entries)
            {
                data.Remove(nameof(AppSettings.WindowBounds));
                repaired = true;
            }
            else
            {
                foreach (var entry in entries.ToArray())
                {
                    bool valid;
                    try
                    {
                        valid = entry.Value?.Deserialize<WindowBounds>()?.IsValid == true;
                    }
                    catch (JsonException)
                    {
                        valid = false;
                    }
                    if (!valid)
                    {
                        entries.Remove(entry.Key);
                        repaired = true;
                    }
                }
            }
        }
        return data.Deserialize<AppSettings>() ?? throw new JsonException("Settings must not be null.");
    }

    private static bool IsUnitValue(double value) => double.IsFinite(value) && value >= 0 && value <= 1;

    private static bool IsValidPlantLayout(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool RemoveInvalid<T>(JsonObject data, string name, Func<T, bool> validate)
    {
        if (!data.TryGetPropertyValue(name, out JsonNode? node))
            return false;
        if (node is JsonValue value && value.TryGetValue<T>(out T? result) && result is not null && validate(result))
            return false;
        data.Remove(name);
        return true;
    }
}

public static class AppSettingsService
{
    private static readonly SettingsStore Store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SensoryStudio",
        "settings.json"));

    public static AppSettings Current { get; private set; } = new();
    public static SettingsNotice? Notice => Store.Notice;
    public static event EventHandler? NoticeChanged
    {
        add => Store.NoticeChanged += value;
        remove => Store.NoticeChanged -= value;
    }

    public static void Load() => Current = Store.Load();
    public static bool Save() => Store.Save(Current);

    public static WindowBounds? GetWindowBounds(string experienceId)
    {
        Current.WindowBounds ??= [];
        Current.WindowBounds.TryGetValue(experienceId, out WindowBounds? bounds);
        return bounds;
    }

    public static void SetWindowBounds(
        string experienceId,
        int left,
        int top,
        int width,
        int height)
    {
        Current.WindowBounds ??= [];
        Current.WindowBounds[experienceId] = new WindowBounds
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
        };
    }
}
