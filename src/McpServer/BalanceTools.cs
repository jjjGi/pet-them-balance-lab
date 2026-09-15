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
        missingPaths = Workspace.MissingPaths(),
        implemented = new[]
        {
            "get_lab_status", "get_balance_config", "list_bot_policies", "run_simulation", "read_run_log",
        },
        notImplementedYet = new[]
        {
            "compare_experiments", "analyze_playtests", "create_balance_candidate", "Create_Balance_Report",
        },
        notes = new[]
        {
            "Simulated runs use deterministic bot policies and are not evidence about human difficulty.",
            "No human playtest logs have been recorded yet, so player analysis is not possible.",
            "Writes are limited to the experiments directory under labRoot.",
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
        [Description("Write JSONL logs and a summary file. Set false for a quick answer with no files on disk.")] bool writeLogs = true)
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
        });
    });

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
