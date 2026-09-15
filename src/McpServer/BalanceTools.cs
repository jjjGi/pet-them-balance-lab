using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PetThem.Combat;

namespace PetThem.BalanceLab.Mcp;

/// <summary>The balance tools an AI client can call over stdio.</summary>
/// <remarks>
/// Every number these tools return is computed from the shared combat rules or read from a file
/// on disk. The tools never estimate, and they say when data is missing instead of filling a gap.
/// </remarks>
[McpServerToolType]
public sealed class BalanceTools
{
    [McpServerTool(Name = "get_lab_status")]
    [Description("Reports which balance-lab tools are implemented, where the game repository is, " +
                 "and which planned tools do not exist yet. Call this before claiming a capability.")]
    public static string GetLabStatus() => Respond(() => new
    {
        server = "pet-them-balance-lab 0.1.0",
        gameRepositoryRoot = Workspace.GameRoot,
        labRoot = Workspace.LabRoot,
        defaultConfigPath = Workspace.DefaultConfigPath,
        experimentsRoot = Workspace.ExperimentsRoot,
        reportsRoot = Workspace.ReportsRoot,
        candidatesRoot = CandidateStore.Root,
        missingPaths = Workspace.MissingPaths(),
        implemented = new[]
        {
            "get_lab_status", "get_balance_config", "list_bot_policies", "run_simulation", "read_run_log",
            "list_experiments", "compare_experiments", "create_balance_candidate",
            "list_balance_candidates", "Create_Balance_Report",
        },
        notImplementedYet = new[] { "analyze_playtests" },
        notes = new[]
        {
            "Simulated runs use deterministic bot policies and are not evidence about human difficulty.",
            "No human playtest logs have been recorded yet, so analyze_playtests would have nothing to read.",
            "Writes are limited to the experiments, reports and candidates directories under labRoot.",
            "create_balance_candidate never edits the game's balance config; applying a candidate is a separate human step.",
        },
    });

    [McpServerTool(Name = "get_balance_config")]
    [Description("Reads the balance config that the Unity game and the simulator share. " +
                 "Returns the file path, the config version, and every value.")]
    public static string GetBalanceConfig(
        [Description("Optional path to a balance config JSON file. Defaults to the game repository's balance-default.json.")]
        string? configPath = null) => Respond(() =>
    {
        string path = ConfigStore.ResolvePath(configPath);
        BalanceConfig config = ConfigStore.Load(path);
        return new
        {
            path,
            version = config.version,
            owner = "The game repository owns this file. The lab reads it and never edits it in place.",
            config,
        };
    });

    [McpServerTool(Name = "list_bot_policies")]
    [Description("Lists the deterministic bot policies run_simulation can use, and what each one does not measure.")]
    public static string ListBotPolicies() => Respond(() => new
    {
        defaultPolicy = BotPolicies.Default,
        policies = BotPolicies.All.Select(policy => new { id = policy.Id, description = policy.Description }),
        limitation = "These bots are reproducible smoke tests. None of them models human reaction time, " +
                     "aim, or fatigue, so their win rate is not the player's win rate.",
    });

    [McpServerTool(Name = "run_simulation")]
    [Description("Runs the shared combat rules with a bot policy for a list of seeds, writes one JSONL log " +
                 "per run plus a summary JSON, and returns the results and aggregates computed from them.")]
    public static string RunSimulation(
        [Description("Number of runs, 1 to 200. Run i uses seed + i.")] int runs = 5,
        [Description("First random seed. The same seed and policy reproduce the same run.")] int seed = 42,
        [Description("Bot policy id from list_bot_policies. Defaults to orbit-auto-punch-v1.")] string? policy = null,
        [Description("Optional balance config path. Defaults to the game repository's balance-default.json.")] string? configPath = null,
        [Description("Output folder name under the lab's experiments directory. Paths outside it are rejected.")] string? outputDirectory = null,
        [Description("Write JSONL logs and a summary file. Set false for a quick answer with no files on disk.")] bool writeLogs = true,
        [Description("Short note about why this experiment was run. Shown in comparisons and reports.")] string? label = null)
        => Respond(() =>
    {
        string path = ConfigStore.ResolvePath(configPath);
        BalanceConfig config = ConfigStore.Load(path);
        string output = writeLogs
            ? PathGuard.ResolveOutputDirectory(outputDirectory ?? "mcp", Workspace.ExperimentsRoot)
            : "";
        return SimulationRunner.Run(new SimulationRequest
        {
            Config = config,
            ConfigPath = path,
            PolicyId = policy ?? BotPolicies.Default,
            Runs = runs,
            Seed = seed,
            OutputDirectory = output,
            WriteLogs = writeLogs,
            Label = label ?? "",
        });
    });

    [McpServerTool(Name = "list_experiments")]
    [Description("Lists stored experiments newest first, with the id, policy, config version and headline " +
                 "numbers. Use this to find ids for compare_experiments and Create_Balance_Report.")]
    public static string ListExperiments(
        [Description("Maximum number of experiments to return.")] int limit = 25)
        => Respond(() => new
        {
            experimentsRoot = Workspace.ExperimentsRoot,
            experiments = ExperimentStore.List(limit: limit),
        });

    [McpServerTool(Name = "compare_experiments")]
    [Description("Compares two experiments seed by seed and reports how each metric moved. Refuses to " +
                 "compare runs that are not comparable (different policy, run length, or no shared seeds) " +
                 "instead of returning a misleading difference.")]
    public static string CompareExperiments(
        [Description("Experiment id or summary file path used as the baseline.")] string baseline,
        [Description("Experiment id or summary file path being evaluated against the baseline.")] string candidate)
        => Respond(() => ExperimentStore.Compare(
            ExperimentStore.Load(baseline), ExperimentStore.Load(candidate)));

    [McpServerTool(Name = "create_balance_candidate")]
    [Description("Saves a proposed balance change as a new candidate file with its rationale. Never edits " +
                 "the game's balance config; applying a candidate to the game is a separate human step. " +
                 "The candidate is validated against the combat rules before it is stored.")]
    public static string CreateBalanceCandidate(
        [Description("Field changes as a JSON object of balance field name to new number, " +
                     "for example {\"punchRange\": 2.0, \"gruntSpeed\": 1.6}.")]
        string changes,
        [Description("Why this change is being proposed, and what it is expected to affect.")] string rationale,
        [Description("Optional base config path. Defaults to the game's balance-default.json.")] string? baseConfigPath = null,
        [Description("Optional candidate id. Letters, digits, '-' and '_' only. Generated when omitted.")] string? candidateId = null)
        => Respond(() =>
    {
        Dictionary<string, double> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, double>>(changes, ConfigStore.Json)
                ?? throw new ArgumentException("changes is empty.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"changes must be a JSON object of field name to number. {exception.Message}");
        }
        return CandidateStore.Create(parsed, rationale, baseConfigPath, candidateId);
    });

    [McpServerTool(Name = "list_balance_candidates")]
    [Description("Lists saved balance candidates with their changes and rationale.")]
    public static string ListBalanceCandidates() => Respond(() => new
    {
        candidatesRoot = CandidateStore.Root,
        editableFields = CandidateStore.EditableFields,
        candidates = CandidateStore.List(),
    });

    [McpServerTool(Name = "Create_Balance_Report")]
    [Description("Writes a self-contained HTML balance report from recorded experiments. Opens with no " +
                 "internet connection and no server. Every figure is computed from the runs, and the " +
                 "report states what the data cannot answer instead of filling the gap.")]
    public static string CreateBalanceReport(
        [Description("Experiment id used as the baseline.")] string baselineExperimentId,
        [Description("Report title shown at the top of the page.")] string? title = null,
        [Description("Comma-separated experiment ids to compare against the baseline.")] string? candidateExperimentIds = null,
        [Description("The question this report should answer, in plain language.")] string? question = null,
        [Description("Output file name under the reports directory. Generated when omitted.")] string? outputPath = null)
        => Respond(() => ReportGenerator.Create(new ReportRequest
        {
            Title = string.IsNullOrWhiteSpace(title) ? "PET THEM! 밸런스 보고서" : title,
            BaselineExperimentId = baselineExperimentId,
            CandidateExperimentIds = (candidateExperimentIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Question = question ?? "",
            OutputPath = outputPath,
        }));

    [McpServerTool(Name = "read_run_log")]
    [Description("Reads one JSONL run log and reports what it actually contains: event counts, kills, damage, " +
                 "and the end reason. Flags incomplete logs instead of treating a missing end as a death.")]
    public static string ReadRunLog(
        [Description("Path to a .jsonl run log written by the simulator or by the game.")] string path)
        => Respond(() => RunLogReader.Read(path));

    /// <summary>Serializes a result, turning expected failures into a readable error instead of a stack trace.</summary>
    private static string Respond<T>(Func<T> work)
    {
        try
        {
            return JsonSerializer.Serialize(work(), ConfigStore.PrettyJson);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException
                                          or UnauthorizedAccessException or OverflowException or FormatException)
        {
            return JsonSerializer.Serialize(new
            {
                error = exception.GetType().Name,
                message = exception.Message,
                hint = "Check get_lab_status for the paths this server can reach.",
            }, ConfigStore.PrettyJson);
        }
    }
}
