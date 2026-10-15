using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace SensoryStudio.WinUI.Services;

internal sealed record GardenMessage(
    string Type,
    string? Layout = null,
    bool Active = false,
    int Count = 0,
    string? Interaction = null,
    double Intensity = 1)
{
    internal const int MaximumMessageBytes = 4 * 1024 * 1024;

    public static bool TryParse(
        string source,
        string json,
        [NotNullWhen(true)] out GardenMessage? message,
        out string error)
    {
        message = null;
        error = "";
        if (!LocalWebViewPolicy.IsGardenDocument(source))
        {
            error = "Untrusted source document.";
            return false;
        }
        if (json.Length > MaximumMessageBytes || Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
        {
            error = "Message exceeds the 4 MiB limit.";
            return false;
        }
        try
        {
            using JsonDocument envelope = JsonDocument.Parse(json);
            if (envelope.RootElement.ValueKind != JsonValueKind.String)
                throw new JsonException();
            string payload = envelope.RootElement.GetString()!;
            if (payload.StartsWith("rake rawMoves=", StringComparison.Ordinal) ||
                payload.StartsWith("guide overlay=", StringComparison.Ordinal))
            {
                message = new("diagnostic");
                return true;
            }
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            string type = RequiredString(root, "type");
            message = type switch
            {
                "ready" => new(type),
                "plant-layout" when root.TryGetProperty("layout", out JsonElement layout) &&
                    layout.ValueKind == JsonValueKind.Object => new(type, Layout: layout.GetRawText()),
                "plant-editor" or "rock-drop-proximity" => new(type, Active: RequiredBoolean(root, "active")),
                "rock-inventory" when root.TryGetProperty("count", out JsonElement count) &&
                    count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int number)
                    => new(type, Count: Math.Clamp(number, 0, 3)),
                "interaction" => ParseInteraction(root),
                _ => throw new JsonException(),
            };
            return true;
        }
        catch (JsonException)
        {
            error = "Invalid JSON, message type, or field value.";
            return false;
        }
    }

    private static GardenMessage ParseInteraction(JsonElement root)
    {
        string interaction = RequiredString(root, "interaction");
        if (interaction is not ("rake" or "collision" or "rock-aligned" or "rock-picked-up" or
            "rock-drag-start" or "grass" or "stack-failed" or "stack-complete"))
            throw new JsonException();
        double intensity = 1;
        if (root.TryGetProperty("detail", out JsonElement detail))
        {
            if (detail.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            if (detail.TryGetProperty("intensity", out JsonElement value))
            {
                if (value.ValueKind != JsonValueKind.Number ||
                    !value.TryGetDouble(out double parsed) || !double.IsFinite(parsed))
                    throw new JsonException();
                intensity = Math.Clamp(parsed, 0, 1);
            }
        }
        return new("interaction", Interaction: interaction, Intensity: intensity);
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException();

    private static bool RequiredBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new JsonException();
}
