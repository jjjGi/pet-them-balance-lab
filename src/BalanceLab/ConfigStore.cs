using System.Text.Json;
using PetThem.Combat;

namespace PetThem.BalanceLab;

/// <summary>Loads the balance config the game ships with. The game repository owns the file.</summary>
public static class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = false };
    public static readonly JsonSerializerOptions PrettyJson = new() { IncludeFields = true, WriteIndented = true };

    /// <summary>
    /// Loads a balance config, or unwraps the config inside a saved candidate file.
    /// </summary>
    /// <remarks>
    /// The shape is checked before deserializing. A JSON file with none of the expected field names
    /// would otherwise deserialize into an all-defaults config and run silently with the wrong numbers.
    /// </remarks>
    public static BalanceConfig Load(string path)
    {
        string full = PathGuard.ResolveInputFile(path);
        string text = File.ReadAllText(full);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Not valid JSON: {full}. {exception.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Balance config must be a JSON object: {full}");

        // A candidate file keeps its config nested, next to the rationale and the change list.
        if (root.TryGetProperty("candidateId", out _) && root.TryGetProperty("config", out JsonElement nested))
        {
            text = nested.GetRawText();
            root = nested;
        }

        foreach (string required in new[] { "version", "duration", "playerHealth", "punchDamage" })
            if (!root.TryGetProperty(required, out _))
                throw new ArgumentException(
                    $"'{full}' is missing '{required}', so it is not a balance config. " +
                    "Pass the game's balance-default.json, a candidate file, or a config written from one.");

        BalanceConfig config = JsonSerializer.Deserialize<BalanceConfig>(text, Json)
            ?? throw new ArgumentException($"Balance config is empty: {full}");
        config.Validate();
        return config;
    }

    public static BalanceConfig LoadDefault() => Load(Workspace.DefaultConfigPath);

    /// <summary>Resolves an optional path argument to the default config when it is not given.</summary>
    public static string ResolvePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? Workspace.DefaultConfigPath : Path.GetFullPath(path);
}
