using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace SensoryStudio.WinUI.Services;

internal sealed class CrashReportStore(string path, TimeProvider? timeProvider = null)
{
    internal const int MaximumReportBytes = 32 * 1024;
    internal const int MaximumCauses = 4;
    internal const int MaximumCallSites = 8;
    internal const int MaximumNameCharacters = 128;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string _path = Path.GetFullPath(path);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();

    public bool Maintain()
    {
        lock (_gate)
        {
            try
            {
                bool discard;
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    discard = stream.Length > MaximumReportBytes || !IsCurrentReport(stream);
                }
                if (discard)
                {
                    File.Delete(_path);
                    Trace.WriteLine("Discarded expired, legacy, or invalid crash report.");
                }
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                ReportFailure("maintain", exception);
                return false;
            }
        }
    }

    public bool Write(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var causes = new List<Cause>();
        Exception? current = exception;
        while (current is not null && causes.Count < MaximumCauses)
        {
            string type = current.GetType().FullName ?? current.GetType().Name;
            StackFrame[] frames = new StackTrace(current, fNeedFileInfo: false).GetFrames() ?? [];
            bool truncated = type.Length > MaximumNameCharacters || frames.Length > MaximumCallSites;
            string[] callSites = frames.Take(MaximumCallSites).Select(frame =>
            {
                var method = frame.GetMethod();
                string name = method is null ? "<unknown>" : $"{method.DeclaringType?.FullName}.{method.Name}";
                truncated |= name.Length > MaximumNameCharacters;
                return LimitName(name);
            }).ToArray();
            causes.Add(new(LimitName(type), current.HResult, callSites, truncated));
            current = current.InnerException;
        }
        var report = new
        {
            SchemaVersion = 1,
            TimestampUtc = _clock.GetUtcNow(),
            AppVersion = typeof(CrashReportStore).Assembly.GetName().Version?.ToString() ?? "unknown",
            Exceptions = causes,
            Truncated = current is not null || causes.Any(cause => cause.Truncated),
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report);
        if (bytes.Length > MaximumReportBytes)
        {
            Trace.TraceError("Crash report exceeded its byte limit; report was not written.");
            return false;
        }

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // A single best-effort report replaces the previous one; no accumulating backups or temp files.
                using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                return true;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException)
            {
                ReportFailure("write", failure);
                return false;
            }
        }
    }

    private bool IsCurrentReport(Stream stream)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (!HasProperties(root, "SchemaVersion", "TimestampUtc", "AppVersion", "Exceptions", "Truncated") ||
                root.GetProperty("SchemaVersion").ValueKind != JsonValueKind.Number ||
                !root.GetProperty("SchemaVersion").TryGetInt32(out int version) || version != 1 ||
                root.GetProperty("TimestampUtc").ValueKind != JsonValueKind.String ||
                !root.GetProperty("TimestampUtc").TryGetDateTimeOffset(out DateTimeOffset timestamp) ||
                !IsShortString(root.GetProperty("AppVersion"), 64) ||
                !IsBoolean(root.GetProperty("Truncated")))
                return false;

            DateTimeOffset now = _clock.GetUtcNow();
            if (timestamp > now || now - timestamp >= Retention)
                return false;
            JsonElement causes = root.GetProperty("Exceptions");
            if (causes.ValueKind != JsonValueKind.Array || causes.GetArrayLength() is < 1 or > MaximumCauses)
                return false;
            foreach (JsonElement cause in causes.EnumerateArray())
            {
                if (!HasProperties(cause, "Type", "HResult", "CallSites", "Truncated") ||
                    !IsShortString(cause.GetProperty("Type"), MaximumNameCharacters) ||
                    cause.GetProperty("HResult").ValueKind != JsonValueKind.Number ||
                    !cause.GetProperty("HResult").TryGetInt32(out _) ||
                    !IsBoolean(cause.GetProperty("Truncated")))
                    return false;
                JsonElement sites = cause.GetProperty("CallSites");
                if (sites.ValueKind != JsonValueKind.Array || sites.GetArrayLength() > MaximumCallSites ||
                    sites.EnumerateArray().Any(site => !IsShortString(site, MaximumNameCharacters)))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // JsonDocument can defer invalid escaped-surrogate errors until names or values are decoded.
            return false;
        }
    }

    private static bool HasProperties(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Count() == names.Length &&
        names.All(name => value.TryGetProperty(name, out _));

    private static bool IsShortString(JsonElement value, int limit)
    {
        if (value.ValueKind != JsonValueKind.String)
            return false;
        int length = value.GetString()!.Length;
        return length > 0 && length <= limit;
    }

    private static bool IsBoolean(JsonElement value) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static string LimitName(string value) => value[..Math.Min(value.Length, MaximumNameCharacters)];

    private static void ReportFailure(string operation, Exception exception) =>
        Trace.TraceError($"Could not {operation} crash report ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}).");

    private sealed record Cause(string Type, int HResult, string[] CallSites, bool Truncated);
}
