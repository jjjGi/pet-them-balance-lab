using PetThem.Combat;
using System.Text.Json;

namespace PetThem.BalanceLab;

/// <summary>One line per stored experiment, cheap enough to list without loading every run.</summary>
public sealed record ExperimentEntry(
    string experimentId, string createdUtc, string label, string policy, string configVersion,
    int runs, double winRate, double meanKills, double meanDamageTaken, string path);

/// <summary>How one metric moved between two experiments, seed by seed.</summary>
/// <remarks>
/// Counts are "increased" and "decreased", not "better" and "worse". More damage taken is not
/// automatically bad and more kills is not automatically good; which direction is wanted is a
/// design decision, and this tool does not make it.
/// </remarks>
/// <remarks>
/// Paired by seed: the same seed produces the same spawns, so the difference is the config change
/// rather than luck. Unpaired means are reported too, but the paired view is the one to trust.
/// </remarks>
public sealed record MetricDelta(
    string metric, double baselineMean, double candidateMean, double meanDelta,
    double minDelta, double maxDelta, int seedsIncreased, int seedsDecreased, int seedsUnchanged);

public sealed record SeedDelta(int seed, double baseline, double candidate, double delta);

public sealed record ComparisonResult(
    string baselineId, string candidateId, string baselineLabel, string candidateLabel,
    string baselineConfigVersion, string candidateConfigVersion,
    IReadOnlyList<int> sharedSeeds, IReadOnlyList<int> baselineOnlySeeds, IReadOnlyList<int> candidateOnlySeeds,
    bool comparable, IReadOnlyList<string> blockers, IReadOnlyList<string> warnings,
    IReadOnlyList<string> configDifferences, IReadOnlyList<MetricDelta> metrics,
    IReadOnlyDictionary<string, IReadOnlyList<SeedDelta>> perSeed, string interpretation);

/// <summary>Finds stored experiments and compares them without inventing numbers.</summary>
public static class ExperimentStore
{
    private const string Interpretation =
        "Differences are computed from recorded runs, paired by seed. This is not a significance test: " +
        "a handful of seeds cannot establish that a change is reliable. Both sides are bot policies, so " +
        "nothing here describes how a human would experience the change.";

    /// <summary>Lists every experiment summary under the experiments root, newest first.</summary>
    public static IReadOnlyList<ExperimentEntry> List(string? root = null, int limit = 100)
    {
        string directory = root is null ? Workspace.ExperimentsRoot : Path.GetFullPath(root);
        if (!Directory.Exists(directory)) return Array.Empty<ExperimentEntry>();

        var entries = new List<ExperimentEntry>();
        foreach (string path in Directory.EnumerateFiles(directory, "exp-*.json", SearchOption.AllDirectories))
        {
            SimulationSummary? summary = TryLoad(path);
            if (summary is null) continue;
            entries.Add(new ExperimentEntry(summary.experimentId, summary.createdUtc, summary.label,
                summary.policy, summary.configVersion, summary.results.Count, summary.aggregates.winRate,
                summary.aggregates.meanKills, summary.aggregates.meanDamageTaken, path));
        }
        return entries.OrderByDescending(entry => entry.createdUtc).Take(Math.Max(1, limit)).ToArray();
    }

    /// <summary>Loads an experiment by id or by path.</summary>
    public static SimulationSummary Load(string idOrPath)
    {
        if (string.IsNullOrWhiteSpace(idOrPath)) throw new ArgumentException("Experiment id or path is empty.");
        if (File.Exists(idOrPath))
            return TryLoad(Path.GetFullPath(idOrPath))
                ?? throw new ArgumentException($"Not a valid experiment summary: {idOrPath}");

        ExperimentEntry? entry = List(limit: int.MaxValue)
            .FirstOrDefault(candidate => candidate.experimentId == idOrPath);
        if (entry is null)
            throw new ArgumentException(
                $"No experiment '{idOrPath}' under {Workspace.ExperimentsRoot}. Use list_experiments to see what exists.");
        return TryLoad(entry.path) ?? throw new ArgumentException($"Could not read {entry.path}");
    }

    public static ComparisonResult Compare(SimulationSummary baseline, SimulationSummary candidate)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (baseline.experimentId == candidate.experimentId)
            blockers.Add("Both sides are the same experiment.");
        if (baseline.policy != candidate.policy)
            blockers.Add($"Different bot policies ('{baseline.policy}' and '{candidate.policy}'). " +
                         "A policy change moves the numbers on its own, so the config change cannot be isolated.");
        if (Math.Abs(baseline.config.duration - candidate.config.duration) > 0.001f)
            blockers.Add($"Different run lengths ({baseline.config.duration}s and {candidate.config.duration}s). " +
                         "Kills and damage are not comparable across different durations.");

        int[] shared = baseline.Seeds.Intersect(candidate.Seeds).OrderBy(seed => seed).ToArray();
        int[] baselineOnly = baseline.Seeds.Except(candidate.Seeds).OrderBy(seed => seed).ToArray();
        int[] candidateOnly = candidate.Seeds.Except(baseline.Seeds).OrderBy(seed => seed).ToArray();

        if (shared.Length == 0)
            blockers.Add("No seed appears in both experiments, so there is nothing to compare run for run.");
        else if (baselineOnly.Length > 0 || candidateOnly.Length > 0)
            warnings.Add($"Only {shared.Length} of {baseline.Seeds.Count} and {candidate.Seeds.Count} seeds " +
                         "are shared. Unshared runs are excluded.");
        if (shared.Length is > 0 and < 5)
            warnings.Add($"{shared.Length} shared seed(s) is a very small sample. Treat the direction as a hint, not a result.");

        string[] differences = ConfigDifferences(baseline.config, candidate.config);
        if (differences.Length == 0)
            warnings.Add("The two configs are identical, so any difference here is noise in the run selection.");

        var metrics = new List<MetricDelta>();
        var perSeed = new Dictionary<string, IReadOnlyList<SeedDelta>>(StringComparer.Ordinal);
        if (shared.Length > 0)
        {
            Add("kills", result => result.kills);
            Add("damageTaken", result => result.damageTaken);
            Add("healthRemaining", result => result.health);
            Add("survivedSeconds", result => result.seconds);
            Add("enemiesSpawned", result => result.enemiesSpawned);
        }

        return new ComparisonResult(baseline.experimentId, candidate.experimentId,
            baseline.label, candidate.label, baseline.configVersion, candidate.configVersion,
            shared, baselineOnly, candidateOnly, blockers.Count == 0, blockers, warnings,
            differences, metrics, perSeed, Interpretation);

        void Add(string metric, Func<RunResult, double> select)
        {
            var deltas = new List<SeedDelta>(shared.Length);
            foreach (int seed in shared)
            {
                double before = select(baseline.results.First(result => result.seed == seed));
                double after = select(candidate.results.First(result => result.seed == seed));
                deltas.Add(new SeedDelta(seed, before, after, after - before));
            }
            perSeed[metric] = deltas;
            metrics.Add(new MetricDelta(metric,
                deltas.Average(delta => delta.baseline),
                deltas.Average(delta => delta.candidate),
                deltas.Average(delta => delta.delta),
                deltas.Min(delta => delta.delta),
                deltas.Max(delta => delta.delta),
                deltas.Count(delta => delta.delta > 0),
                deltas.Count(delta => delta.delta < 0),
                deltas.Count(delta => Math.Abs(delta.delta) < 0.0001)));
        }
    }

    /// <summary>Every balance field whose value differs, as "field: before -> after".</summary>
    public static string[] ConfigDifferences(BalanceConfig baseline, BalanceConfig candidate)
    {
        var differences = new List<string>();
        foreach (System.Reflection.FieldInfo field in typeof(BalanceConfig).GetFields())
        {
            object? before = field.GetValue(baseline);
            object? after = field.GetValue(candidate);
            if (!Equals(before, after)) differences.Add($"{field.Name}: {before} -> {after}");
        }
        return differences.ToArray();
    }

    private static SimulationSummary? TryLoad(string path)
    {
        try
        {
            SimulationSummary? summary =
                JsonSerializer.Deserialize<SimulationSummary>(File.ReadAllText(path), ConfigStore.Json);
            return summary is null || string.IsNullOrWhiteSpace(summary.experimentId) ? null : summary;
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
