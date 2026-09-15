using System.Reflection;

namespace PetThem.BalanceLab;

/// <summary>Resolves the directories this tool reads from and writes to.</summary>
/// <remarks>
/// The combat rules and the balance config live in the game repository (jjjGi/pet-them).
/// This repository never copies them; it points at them.
/// </remarks>
public static class Workspace
{
    /// <summary>Root of this repository. Generated output stays inside it.</summary>
    public static string LabRoot { get; } = Resolve("PetThemLabRoot", "PETTHEM_LAB_ROOT");

    /// <summary>Root of the game repository that owns the combat rules and the balance config.</summary>
    public static string GameRoot { get; } = Resolve("PetThemGameRoot", "PETTHEM_GAME_ROOT");

    public static string DefaultConfigPath =>
        Path.Combine(GameRoot, "game", "Assets", "Resources", "balance-default.json");

    public static string ExperimentsRoot => Path.Combine(LabRoot, "experiments");

    public static string ReportsRoot => Path.Combine(LabRoot, "reports");

    /// <summary>Reports which required paths are missing, so tools can say so instead of guessing.</summary>
    public static IReadOnlyList<string> MissingPaths()
    {
        var missing = new List<string>();
        if (!Directory.Exists(GameRoot)) missing.Add($"game repository root: {GameRoot}");
        else if (!File.Exists(DefaultConfigPath)) missing.Add($"default balance config: {DefaultConfigPath}");
        return missing;
    }

    private static string Resolve(string metadataKey, string environmentKey)
    {
        string? value = Environment.GetEnvironmentVariable(environmentKey);
        if (string.IsNullOrWhiteSpace(value)) value = Metadata(metadataKey);
        if (string.IsNullOrWhiteSpace(value)) value = AppContext.BaseDirectory;
        return Path.GetFullPath(value);
    }

    private static string? Metadata(string key) => typeof(Workspace).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == key)?.Value;
}
