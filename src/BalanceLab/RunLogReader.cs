using System.Text.Json;

namespace PetThem.BalanceLab;

/// <summary>What a single JSONL run log actually contains, counted from the file.</summary>
public sealed record RunLogSummary(
    string path, long bytes, string? runId, string? source, string? policy, int? seed,
    string? configVersion, string? buildVersion, int eventCount,
    IReadOnlyDictionary<string, int> eventCounts, int killEvents, double damageDealt, double damageTaken,
    string? endReason, double? endTimeSeconds, int? endKills, bool complete, IReadOnlyList<string> warnings);

/// <summary>One recorded snapshot: what the run looked like at that moment.</summary>
public sealed record TimelinePoint(double time, double health, int alive, double kills);

public sealed record RunTimeline(int seed, string path, IReadOnlyList<TimelinePoint> points);

/// <summary>Reads run logs back so reported numbers can be traced to the recorded events.</summary>
/// <remarks>
/// A log without a <c>run_end</c> line is incomplete, not a death: the process may have been killed.
/// See docs/telemetry.md in the game repository for the field meanings.
/// </remarks>
public static class RunLogReader
{
    public const long MaxBytes = 64L * 1024 * 1024;

    public static RunLogSummary Read(string path)
    {
        string full = PathGuard.ResolveInputFile(path);
        var info = new FileInfo(full);
        if (info.Length > MaxBytes)
            throw new ArgumentException($"Run log is larger than the {MaxBytes / (1024 * 1024)} MB read limit: {full}");

        var warnings = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? runId = null, source = null, policy = null, configVersion = null, buildVersion = null;
        int? seed = null;
        int events = 0, killEvents = 0;
        double damageDealt = 0, damageTaken = 0;
        string? endReason = null;
        double? endTime = null;
        int? endKills = null;
        int lineNumber = 0;

        foreach (string line in File.ReadLines(full))
        {
            lineNumber++;
            if (line.Length == 0) continue;
            JsonElement element;
            try
            {
                element = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException exception)
            {
                warnings.Add($"line {lineNumber} is not valid JSON: {exception.Message}");
                continue;
            }

            string type = Text(element, "type") ?? "";
            if (lineNumber == 1 && type != "run_start")
                warnings.Add("first line is not run_start, so the run header is missing.");

            if (type == "run_start")
            {
                if (runId is not null) warnings.Add($"line {lineNumber} contains a second run_start.");
                runId = Text(element, "runId");
                source = Text(element, "source");
                policy = Text(element, "policy");
                buildVersion = Text(element, "buildVersion");
                seed = Integer(element, "seed");
                configVersion = ConfigVersion(Text(element, "configJson"), warnings);
                continue;
            }

            events++;
            counts[type] = counts.GetValueOrDefault(type) + 1;
            double value = Number(element, "value") ?? 0;
            switch (type)
            {
                case "kill": killEvents++; break;
                case "damage": damageDealt += value; break;
                case "hurt": damageTaken += value; break;
                case "run_end":
                    if (endReason is not null) warnings.Add($"line {lineNumber} contains a second run_end.");
                    endReason = Text(element, "source");
                    endTime = Number(element, "time");
                    endKills = (int?)value;
                    break;
            }
        }

        if (runId is null) warnings.Add("no run_start line: the log has no header.");
        if (endReason is null)
            warnings.Add("no run_end line: the run is incomplete and must not be read as a death.");
        else if (endKills is not null && endKills != killEvents)
            warnings.Add($"run_end reports {endKills} kills but the log contains {killEvents} kill events.");

        return new RunLogSummary(full, info.Length, runId, source, policy, seed, configVersion, buildVersion,
            events, counts, killEvents, damageDealt, damageTaken, endReason, endTime, endKills,
            runId is not null && endReason is not null, warnings);
    }

    /// <summary>
    /// Extracts the recorded snapshots as a time series. Charts are drawn from these points only,
    /// so nothing in a report is interpolated from a number that was never written down.
    /// </summary>
    public static RunTimeline ReadTimeline(string path, int seed)
    {
        string full = PathGuard.ResolveInputFile(path);
        var points = new List<TimelinePoint>();
        foreach (string line in File.ReadLines(full))
        {
            if (line.Length == 0 || !line.Contains("\"snapshot\"", StringComparison.Ordinal)) continue;
            JsonElement element;
            try
            {
                element = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException)
            {
                continue;
            }
            if (Text(element, "type") != "snapshot") continue;
            points.Add(new TimelinePoint(
                Number(element, "time") ?? 0,
                Number(element, "health") ?? 0,
                Integer(element, "alive") ?? 0,
                Number(element, "value") ?? 0));
        }
        return new RunTimeline(seed, full, points);
    }

    private static string? ConfigVersion(string? configJson, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            return Text(JsonDocument.Parse(configJson).RootElement, "version");
        }
        catch (JsonException)
        {
            warnings.Add("run_start.configJson is not valid JSON.");
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result) ? result : null;
}
