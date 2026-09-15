using System.Globalization;
using System.Text;
using System.Text.Json;
using PetThem.Combat;

namespace PetThem.BalanceLab;

public sealed class SimulationRequest
{
    public required BalanceConfig Config { get; init; }
    public required string ConfigPath { get; init; }
    public string PolicyId { get; init; } = BotPolicies.Default;
    public int Seed { get; init; } = 42;
    public int Runs { get; init; } = 5;
    /// <summary>Already resolved by <see cref="PathGuard"/>. Ignored when <see cref="WriteLogs"/> is false.</summary>
    public string OutputDirectory { get; init; } = "";
    public bool WriteLogs { get; init; } = true;
    public string BuildVersion { get; init; } = "0.1.0";
    /// <summary>Short human note about why this experiment was run. Shown in comparisons and reports.</summary>
    public string Label { get; init; } = "";
}

public sealed record RunResult(
    int seed, string state, float seconds, int kills, float health,
    float damageTaken, int enemiesSpawned, string? log);

/// <summary>Values computed from the runs in code. No estimate, no model, no rounding of the source data.</summary>
public sealed record SimulationAggregates(
    int runs, double winRate, double meanSeconds, double meanKills, int minKills, int maxKills,
    double meanHealthRemaining, double meanDamageTaken, int runsWithoutDamage);

public sealed record SimulationSummary(
    string schemaVersion, string experimentId, string createdUtc, string label,
    string source, string policy, string policyDescription, string limitation,
    string configPath, string configVersion, int seed, BalanceConfig config,
    SimulationAggregates aggregates, IReadOnlyList<RunResult> results, string? summaryPath)
{
    /// <summary>The seeds this experiment ran, in order. Two experiments are only comparable on shared seeds.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<int> Seeds => results.Select(result => result.seed).ToArray();
}

/// <summary>Runs the shared combat rules with a bot policy and records the result.</summary>
/// <remarks>The CLI and the MCP server both call this, so both produce identical logs.</remarks>
public static class SimulationRunner
{
    public const int MaxRuns = 200;
    public const float MaxDurationSeconds = 3600;

    private const string Limitation =
        "Synthetic movement and auto-punch on a fixed 60 Hz step. Does not measure mobile input, " +
        "reaction time, fatigue, or fun, and is not evidence about difficulty for human players.";

    public static SimulationSummary Run(SimulationRequest request)
    {
        if (request.Runs < 1 || request.Runs > MaxRuns)
            throw new ArgumentException($"runs must be between 1 and {MaxRuns}.");
        request.Config.Validate();
        if (request.Config.duration > MaxDurationSeconds)
            throw new ArgumentException($"Simulation duration limit is {MaxDurationSeconds} seconds.");

        IBotPolicy policy = BotPolicies.Get(request.PolicyId);
        if (request.WriteLogs) Directory.CreateDirectory(request.OutputDirectory);

        var results = new List<RunResult>(request.Runs);
        for (int run = 0; run < request.Runs; run++)
            results.Add(Single(request, policy, checked(request.Seed + run)));

        var aggregates = new SimulationAggregates(
            results.Count,
            results.Count(r => r.state == nameof(RunState.Won)) / (double)results.Count,
            results.Average(r => (double)r.seconds),
            results.Average(r => (double)r.kills),
            results.Min(r => r.kills),
            results.Max(r => r.kills),
            results.Average(r => (double)r.health),
            results.Average(r => (double)r.damageTaken),
            results.Count(r => r.damageTaken <= 0));

        DateTime now = DateTime.UtcNow;
        string experimentId = "exp-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + policy.Id + "-" + Guid.NewGuid().ToString("N")[..6];

        var summary = new SimulationSummary("1", experimentId,
            now.ToString("o", CultureInfo.InvariantCulture), request.Label,
            "simulation", policy.Id, policy.Description, Limitation,
            request.ConfigPath, request.Config.version, request.Seed, request.Config,
            aggregates, results, null);

        if (!request.WriteLogs) return summary;

        string summaryPath = Path.Combine(request.OutputDirectory, experimentId + ".json");
        summary = summary with { summaryPath = summaryPath };
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, ConfigStore.PrettyJson));
        return summary;
    }

    private static RunResult Single(SimulationRequest request, IBotPolicy policy, int runSeed)
    {
        BalanceConfig config = request.Config;
        var world = new CombatWorld(config, runSeed);
        string id = policy.Id + "-" + runSeed + "-" + Guid.NewGuid().ToString("N")[..8];
        string? file = request.WriteLogs ? Path.Combine(request.OutputDirectory, id + ".jsonl") : null;

        StreamWriter? writer = file is null ? null : new StreamWriter(file, false, new UTF8Encoding(false));
        float damageTaken = 0;
        int spawned = 0;
        try
        {
            writer?.WriteLine(JsonSerializer.Serialize(new
            {
                type = "run_start", schemaVersion = "1", source = "simulation", policy = policy.Id,
                runId = id, seed = runSeed, fixedStep = CombatWorld.StepSeconds,
                buildVersion = request.BuildVersion,
                configJson = JsonSerializer.Serialize(config, ConfigStore.Json),
            }, ConfigStore.Json));

            while (world.State == RunState.Playing)
            {
                world.Step(policy.Decide(world, config));
                foreach (CombatEvent combatEvent in world.Events)
                {
                    if (combatEvent.type == "hurt") damageTaken += combatEvent.value;
                    else if (combatEvent.type == "spawn") spawned++;
                    writer?.WriteLine(JsonSerializer.Serialize(combatEvent, ConfigStore.Json));
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }

        return new RunResult(runSeed, world.State.ToString(), world.Time, world.Kills, world.Health,
            damageTaken, spawned, file is null ? null : Path.GetFileName(file));
    }
}
