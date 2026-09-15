using System.Text.Json;
using PetThem.Combat;

namespace PetThem.BalanceLab;

/// <summary>Loads the balance config the game ships with. The game repository owns the file.</summary>
public static class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = false };
    public static readonly JsonSerializerOptions PrettyJson = new() { IncludeFields = true, WriteIndented = true };

    public static BalanceConfig Load(string path)
    {
        string full = PathGuard.ResolveInputFile(path);
        BalanceConfig config = JsonSerializer.Deserialize<BalanceConfig>(File.ReadAllText(full), Json)
            ?? throw new ArgumentException($"Balance config is empty: {full}");
        config.Validate();
        return config;
    }

    public static BalanceConfig LoadDefault() => Load(Workspace.DefaultConfigPath);

    /// <summary>Resolves an optional path argument to the default config when it is not given.</summary>
    public static string ResolvePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Workspace.DefaultConfigPath : Path.GetFullPath(path);
}
