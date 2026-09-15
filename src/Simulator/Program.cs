using System.Globalization;
using System.Text.Json;
using PetThem.BalanceLab;
using PetThem.Combat;

// Bot simulation runner. Same combat rules as the game, no renderer and no wall-clock time.
try
{
    if (args.Contains("--help") || args.Contains("-h"))
    {
        Usage();
        return;
    }

    string configPath = ConfigStore.ResolvePath(Value("--config"));
    string policy = Value("--policy") ?? BotPolicies.Default;
    int runs = Integer("--runs", 5);
    int seed = Integer("--seed", 42);
    string output = PathGuard.ResolveOutputDirectory(Value("--output") ?? "smoke", Workspace.ExperimentsRoot);

    BalanceConfig config = ConfigStore.Load(configPath);
    SimulationSummary summary = SimulationRunner.Run(new SimulationRequest
    {
        Config = config,
        ConfigPath = configPath,
        PolicyId = policy,
        Runs = runs,
        Seed = seed,
        OutputDirectory = output,
    });

    foreach (RunResult result in summary.results)
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "seed {0,-6} {1,-9} {2,6:0.0}s  kills {3,4}  health {4,5:0.#}  damage taken {5,5:0.#}",
            result.seed, result.state, result.seconds, result.kills, result.health, result.damageTaken));

    Console.WriteLine();
    Console.WriteLine(JsonSerializer.Serialize(summary.aggregates, ConfigStore.PrettyJson));
    Console.WriteLine(summary.limitation);
    Console.WriteLine(summary.summaryPath);
}
catch (Exception exception) when (exception is IOException || exception is ArgumentException ||
                                  exception is JsonException || exception is OverflowException ||
                                  exception is FormatException || exception is UnauthorizedAccessException)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}

void Usage()
{
    Console.WriteLine("""
        PET THEM! Balance Lab simulator

          --config <path>    Balance config JSON. Default: the game repository's balance-default.json.
          --policy <id>      Bot policy. Default: orbit-auto-punch-v1.
          --runs <n>         Number of runs, 1-200. Default: 5.
          --seed <n>         First seed; run i uses seed + i. Default: 42.
          --output <dir>     Output directory under experiments/. Default: smoke.

        Policies:
        """);
    foreach (IBotPolicy policy in BotPolicies.All)
        Console.WriteLine($"  {policy.Id,-24} {policy.Description}");
}

string? Value(string flag)
{
    int index = Array.IndexOf(args, flag);
    if (index < 0) return null;
    if (index + 1 >= args.Length) throw new ArgumentException("Missing value for " + flag);
    return args[index + 1];
}

int Integer(string flag, int fallback)
{
    string? raw = Value(flag);
    return raw is null ? fallback : int.Parse(raw, CultureInfo.InvariantCulture);
}
