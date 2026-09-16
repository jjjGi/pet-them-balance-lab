using System.Text.Json;
using PetThem.BalanceLab;

internal static class PlaytestChecks
{
    public static void Run(Action<string, Action> check, string scratch)
    {
        string death = Fixture(scratch, "client-death", 30, "death");
        string abandoned = Fixture(scratch, "client-abandoned", 15, "abandoned");
        string survived = Fixture(scratch, "client-survived", 65, "survived");
        string incomplete = Fixture(scratch, "client-incomplete", 45, null);

        check("playtest bins use reached denominators, exposure and distinct terminations", () =>
        {
            var result = PlaytestAnalyzer.Analyze(new[] { death, abandoned, survived, incomplete });
            Require(result.includedRuns == 4 && result.excluded.Count == 0 && result.groups.Count == 1);
            var bins = result.groups[0].intervals;
            Require(bins.Count == 3);
            Require(bins[0].reached == 4 && bins[0].abandoned == 1 && bins[0].deaths == 0);
            Near(bins[0].exposureSeconds, 105);
            Near(bins[0].damageTaken, 20);
            Near(bins[0].damagePerObservedMinute!.Value, 20.0 / 105 * 60);
            Require(bins[1].reached == 3 && bins[1].deaths == 1 && bins[1].incomplete == 1);
            Near(bins[1].deathFractionOfReached, 1.0 / 3);
            Require(bins[2].reached == 1 && bins[2].survived == 1);
            var run = result.groups[0].runs[0];
            Near(run.damageBySource["punch"], 7);
            Near(run.damageBySource["pet"], 3);
            Require(run.kills == 1 && run.attacksBySource["punch"] == 1);
            Require(run.events[^1].type == "snapshot"); // Unity's terminal-event ordering.
            Require(result.groups[0].runs.Single(r => r.outcome == "incomplete").warnings.Count > 0);
        });

        check("client grouping uses full config, build and platform rather than version label alone", () =>
        {
            string differentConfig = Fixture(scratch, "different-config", 30, "death", range: 9);
            string differentBuild = Fixture(scratch, "different-build", 30, "death", build: "0.2");
            string differentPlatform = Fixture(scratch, "different-platform", 30, "death", platform: "Android");
            string reordered = Fixture(scratch, "reordered-config", 30, "death", reorder: true);
            var result = PlaytestAnalyzer.Analyze(new[] { death, differentConfig, differentBuild, differentPlatform, reordered });
            Require(result.includedRuns == 5 && result.groups.Count == 4);
            Require(result.groups[0].runs.Count == 2);
        });

        check("bot, duplicate, malformed and incompatible playtest inputs are explicitly excluded", () =>
        {
            string bot = Fixture(scratch, "bot", 30, "death", source: "simulation");
            string copy = Path.Combine(scratch, "copied-client.jsonl");
            File.Copy(death, copy);
            string malformed = Path.Combine(scratch, "malformed-client.jsonl");
            File.WriteAllText(malformed, File.ReadAllText(death) + "\n{broken");
            string array = Path.Combine(scratch, "array-client.jsonl");
            File.WriteAllText(array, "[]");
            string wrongKills = Path.Combine(scratch, "wrong-kills.jsonl");
            File.WriteAllText(wrongKills, File.ReadAllText(death).Replace("\"value\":1", "\"value\":2"));
            string wrongSchema = Path.Combine(scratch, "wrong-schema.jsonl");
            File.WriteAllText(wrongSchema, File.ReadAllText(death).Replace("\"schemaVersion\":\"1\"", "\"schemaVersion\":\"2\""));
            var result = PlaytestAnalyzer.Analyze(new[] { death, death, copy, bot, malformed, array, wrongKills, wrongSchema });
            Require(result.includedRuns == 1 && result.excluded.Count == 7);
            Require(result.excluded.All(e => !string.IsNullOrWhiteSpace(e.reason)));
            Require(!JsonSerializer.Serialize(result).Contains("\"events\"")); // no raw event dump over MCP
        });

        check("playtest request limits and missing observations are handled", () =>
        {
            Reject(() => PlaytestAnalyzer.Analyze(Array.Empty<string>()));
            Reject(() => PlaytestAnalyzer.Analyze(Enumerable.Repeat(death, 51).ToArray()));
            Reject(() => PlaytestAnalyzer.Analyze(new[] { death }, 4));
            Reject(() => PlaytestAnalyzer.Analyze(new[] { death }, 121));
            var missing = PlaytestAnalyzer.Analyze(new[] { Path.Combine(scratch, "absent.jsonl") });
            Require(missing.includedRuns == 0 && missing.groups.Count == 0 && missing.excluded.Count == 1);
            string noSnapshots = Path.Combine(scratch, "no-snapshots.jsonl");
            File.WriteAllLines(noSnapshots, File.ReadAllLines(death).Where(l => !l.Contains("\"snapshot\"")));
            var result = PlaytestAnalyzer.Analyze(new[] { noSnapshots });
            Require(result.groups[0].intervals.All(i => i.meanAliveSnapshot is null));
            Require(result.groups[0].runs[0].warnings.Count == 1);
        });

        check("client reports render computed data, escape strings and stay offline", () =>
        {
            string fileName = "playtest-check-" + Guid.NewGuid().ToString("N") + ".html";
            string hostile = Fixture(scratch, "hostile", 30, "death", build: "<img src=x onerror=alert(1)>");
            ReportResult report = ReportGenerator.Create(new ReportRequest
            {
                PlaytestPaths = new[] { hostile, abandoned, incomplete },
                Title = "<script>alert('title')</script>", Question = "피해 & 적 누적", OutputPath = fileName
            });
            try
            {
                string html = File.ReadAllText(report.path);
                Require(html.Contains("&lt;script&gt;") && html.Contains("&lt;img"));
                Require(!html.Contains("<script", StringComparison.OrdinalIgnoreCase));
                Require(!html.Contains("<img", StringComparison.OrdinalIgnoreCase));
                Require(!html.Contains("https://") && !html.Contains("http://") && !html.Contains("<iframe"));
                Require(html.Contains("<svg") && html.Contains("<details open>"));
                Require(html.Contains("중도 종료") && html.Contains("불완전 기록") && html.Contains("피해/분"));
                Require(!html.Contains("사람의 플레이 기록이 0건"));
                Require(!html.Contains("{\"type\":\"run_start\""));
            }
            finally { File.Delete(report.path); }
            Reject(() => ReportGenerator.Create(new ReportRequest { PlaytestPaths = new[] { death }, OutputPath = "../escape.html" }));
            Reject(() => ReportGenerator.Create(new ReportRequest { PlaytestPaths = new[] { death }, BaselineExperimentId = "bot-id" }));
            Reject(() => ReportGenerator.Create(new ReportRequest()));
        });
    }

    private static string Fixture(string directory, string id, double end, string? outcome,
        string source = "human", double range = 2.6, string build = "0.1", string platform = "WindowsEditor", bool reorder = false)
    {
        string path = Path.Combine(directory, id + ".jsonl");
        string config = reorder
            ? JsonSerializer.Serialize(new { punchRange = range, version = "fixture-v1" })
            : JsonSerializer.Serialize(new { version = "fixture-v1", punchRange = range });
        var lines = new List<string> { JsonSerializer.Serialize(new
        {
            type = "run_start", schemaVersion = "1", source, runId = id, buildVersion = build,
            platform, fixedStep = 1.0 / 60, configJson = config
        }) };
        void Event(string type, double time, double value, string actor, double health = 100, int alive = 2) =>
            lines.Add(JsonSerializer.Serialize(new { type, time, value, source = actor, health, alive }));
        Event("snapshot", 0, 0, "world");
        Event("attack", 5, 0, "punch");
        Event("damage", 5, 7, "punch");
        Event("damage", 5, 3, "pet");
        Event("kill", 5, 1, "pet");
        Event("hurt", 10, 5, "Grunt", 95);
        if (outcome is not null) Event("run_end", end, 1, outcome, outcome == "death" ? 0 : 95);
        Event("snapshot", end, 1, "world", outcome == "death" ? 0 : 95, 4);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static void Require(bool value) { if (!value) throw new Exception("Playtest assertion failed."); }
    private static void Near(double actual, double expected) => Require(Math.Abs(actual - expected) < 0.00001);
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected rejection.");
    }
}
