using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetThem.BalanceLab;

public sealed record PlaytestEvent(string type, double time, double value, string source,
    double health, int alive);
public sealed record PlaytestRun(string path, string runId, string buildVersion, string platform,
    string configVersion, string configHash, double fixedStep, string outcome, double observedSeconds,
    int kills, double damageTaken, IReadOnlyDictionary<string, double> damageBySource,
    [property: JsonIgnore] IReadOnlyList<PlaytestEvent> events, IReadOnlyList<string> warnings)
{
    public IReadOnlyDictionary<string, int> attacksBySource => events.Where(e => e.type == "attack")
        .GroupBy(e => e.source).ToDictionary(g => g.Key, g => g.Count());
}
public sealed record ExcludedPlaytest(string path, string reason);
public sealed record PlaytestInterval(double startSeconds, double endSeconds, int reached,
    int deaths, int survived, int abandoned, int incomplete, double deathFractionOfReached,
    double exposureSeconds, double damageTaken, double? damagePerObservedMinute,
    double? meanAliveSnapshot, int snapshotCount);
public sealed record PlaytestGroup(string id, string buildVersion, string platform, string configVersion,
    string configHash, IReadOnlyList<PlaytestRun> runs, IReadOnlyList<PlaytestInterval> intervals);
public sealed record PlaytestAnalysis(int suppliedFiles, int includedRuns,
    IReadOnlyList<ExcludedPlaytest> excluded, IReadOnlyList<PlaytestGroup> groups,
    IReadOnlyList<string> limitations);

/// <summary>Analyzes explicitly selected client logs; never mixes bots into player statistics.</summary>
public static class PlaytestAnalyzer
{
    public const int MaxFiles = 50;
    public const long MaxTotalBytes = 256L * 1024 * 1024;
    public const double MaxSeconds = 3600;

    public static PlaytestAnalysis Analyze(IReadOnlyList<string> paths, int intervalSeconds = 30)
    {
        if (paths.Count is < 1 or > MaxFiles) throw new ArgumentException("Supply 1 to 50 JSONL paths.");
        if (intervalSeconds is < 5 or > 120) throw new ArgumentException("intervalSeconds must be 5 to 120.");
        var runs = new List<PlaytestRun>();
        var excluded = new List<ExcludedPlaytest>();
        var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (string path in paths)
        {
            try
            {
                string full = PathGuard.ResolveInputFile(path);
                if (!seenPaths.Add(full)) throw new ArgumentException("Duplicate path.");
                long bytes = new FileInfo(full).Length;
                if (bytes > RunLogReader.MaxBytes) throw new ArgumentException("File exceeds 64 MB.");
                total += bytes;
                if (total > MaxTotalBytes) throw new ArgumentException("Batch exceeds 256 MB.");
                PlaytestRun run = Read(full);
                if (!seenIds.Add(run.runId)) throw new ArgumentException("Duplicate runId; copied runs are not new participants.");
                runs.Add(run);
            }
            catch (Exception e) when (e is ArgumentException or IOException or JsonException or UnauthorizedAccessException)
            {
                excluded.Add(new(path, e.Message));
            }
        }
        var groups = runs.GroupBy(r => (r.buildVersion, r.platform, r.configHash, r.fixedStep))
            .Select((group, i) => new PlaytestGroup($"group-{i + 1}", group.Key.buildVersion,
                group.Key.platform, group.First().configVersion, group.Key.configHash,
                group.ToArray(), Intervals(group.ToArray(), intervalSeconds))).ToArray();
        return new(paths.Count, runs.Count, excluded, groups, new[]
        {
            "source=human은 클라이언트가 붙인 라벨입니다. 실행 수는 고유 플레이어 수가 아닙니다.",
            "사망 비율은 해당 구간에 도달한 실행을 분모로 합니다. 중도 종료·불완전 기록은 사망도 승리도 아닙니다.",
            "중도 종료 때문에 뒤 구간의 관측 대상이 줄어듭니다. 이 비율은 보정된 생존 확률이 아닙니다.",
            "빌드·플랫폼·설정 전체의 해시·고정 스텝이 다른 기록은 별도 그룹입니다. 그룹 간 수치를 합치지 않습니다.",
            "피해/분은 실제 관측된 게임 시간 기준입니다. 일시정지 시간은 포함하지 않습니다.",
            "적 수는 스냅샷의 단순 평균이며, 피해 기여도는 초과 피해를 제외한 적용 피해입니다.",
            "이 기록만으로 조작 피로·숙련도·성장 정체의 원인을 확정할 수 없습니다. 성장·상점은 아직 분석 대상이 없습니다."
        });
    }

    private static PlaytestRun Read(string path)
    {
        // Bound the read even if a live client appends while we are reading.
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (stream.Length > RunLogReader.MaxBytes) throw new ArgumentException("File exceeds 64 MB.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        using var lines = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        string? id = null, build = null, platform = null, configVersion = null, hash = null;
        double fixedStep = 0, lastTime = 0;
        var events = new List<PlaytestEvent>();
        string outcome = "incomplete";
        bool ended = false;
        int lineNumber = 0;
        while (lines.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > 1024 * 1024) throw new ArgumentException($"Line {lineNumber} exceeds 1 MB.");
            using var doc = JsonDocument.Parse(line);
            JsonElement e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Line {lineNumber} must be an object.");
            string type = Text(e, "type");
            // CombatWorld emits a final snapshot after a death/survival terminal event.
            if (ended && (type != "snapshot" || Number(e, "time") != lastTime || events[^1].type != "run_end"))
                throw new ArgumentException("Only one same-time final snapshot is allowed after run_end.");
            if (type == "run_start")
            {
                if (id is not null) throw new ArgumentException("Multiple run_start records.");
                if (Text(e, "source") != "human") throw new ArgumentException("Only source=human client logs are included; bot/unknown source excluded.");
                if (Text(e, "schemaVersion") != "1") throw new ArgumentException("Unsupported schemaVersion.");
                id = Text(e, "runId"); build = Text(e, "buildVersion"); platform = Text(e, "platform");
                fixedStep = Number(e, "fixedStep");
                if (fixedStep <= 0 || fixedStep > 1) throw new ArgumentException("Invalid fixedStep.");
                using var config = JsonDocument.Parse(Text(e, "configJson"));
                if (config.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("configJson must be an object.");
                configVersion = Text(config.RootElement, "version");
                hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(config.RootElement))));
                continue;
            }
            if (id is null) throw new ArgumentException("Missing run_start before events.");
            if (type is not ("wave" or "spawn" or "attack" or "damage" or "kill" or "hurt" or "snapshot" or "run_end"))
                throw new ArgumentException($"Unsupported event type: {type}.");
            double time = Number(e, "time"), value = Number(e, "value"), health = Number(e, "health"), alive = Number(e, "alive");
            if (time < lastTime || time > MaxSeconds || value < 0 || health < 0 || alive < 0 || alive != Math.Truncate(alive) || alive > int.MaxValue)
                throw new ArgumentException($"Invalid event values or time order at line {lineNumber}.");
            lastTime = time;
            string source = Text(e, "source");
            events.Add(new(type, time, value, source, health, (int)alive));
            if (type == "run_end")
            {
                if (source is not ("survived" or "death" or "abandoned")) throw new ArgumentException("Unknown termination reason.");
                if (value != events.Count(item => item.type == "kill")) throw new ArgumentException("Terminal kill total differs from kill events.");
                outcome = source;
                ended = true;
            }
        }
        if (id is null || events.Count == 0) throw new ArgumentException("No usable client events.");
        var warnings = new List<string>();
        if (!ended) warnings.Add("종료 이벤트 없음: 관측된 구간만 사용합니다. 사망으로 집계하지 않습니다.");
        if (!events.Any(e => e.type == "snapshot")) warnings.Add("스냅샷 없음: 체력·적 수 그래프를 그릴 수 없습니다.");
        return new(path, id, build!, platform!, configVersion!, hash!, fixedStep, outcome, lastTime,
            events.Count(e => e.type == "kill"), events.Where(e => e.type == "hurt").Sum(e => e.value),
            events.Where(e => e.type == "damage").GroupBy(e => e.source).ToDictionary(g => g.Key, g => g.Sum(e => e.value)), events, warnings);
    }

    private static PlaytestInterval[] Intervals(IReadOnlyList<PlaytestRun> runs, int width)
    {
        // [start,end), including a terminal event exactly at a boundary in the next interval.
        int count = (int)Math.Floor(runs.Max(r => r.observedSeconds) / width) + 1;
        return Enumerable.Range(0, count).Select(index =>
        {
            double start = index * width, end = start + width;
            var reached = runs.Where(r => r.observedSeconds >= start).ToArray();
            var stopped = reached.Where(r => r.observedSeconds < end).ToArray();
            var snapshots = reached.SelectMany(r => r.events).Where(e => e.type == "snapshot" && e.time >= start && e.time < end).ToArray();
            double damage = reached.SelectMany(r => r.events).Where(e => e.type == "hurt" && e.time >= start && e.time < end).Sum(e => e.value);
            double exposure = reached.Sum(r => Math.Max(0, Math.Min(end, r.observedSeconds) - start));
            int deaths = stopped.Count(r => r.outcome == "death");
            return new PlaytestInterval(start, end, reached.Length, deaths,
                stopped.Count(r => r.outcome == "survived"), stopped.Count(r => r.outcome == "abandoned"),
                stopped.Count(r => r.outcome == "incomplete"), (double)deaths / reached.Length,
                exposure, damage, exposure > 0 ? damage / exposure * 60 : null,
                snapshots.Length > 0 ? snapshots.Average(e => (double)e.alive) : null, snapshots.Length);
        }).ToArray();
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()! : throw new ArgumentException($"Missing or invalid {name}.");
    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double value) && double.IsFinite(value)
            ? value : throw new ArgumentException($"Missing or invalid finite number: {name}.");
    private static string Canonical(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
        _ => e.GetRawText()
    };
}
