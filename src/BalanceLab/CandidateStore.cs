using System.Globalization;
using System.Text.Json;
using PetThem.Combat;

namespace PetThem.BalanceLab;

public sealed record FieldChange(string field, double from, double to);

public sealed record BalanceCandidate(
    string schemaVersion, string candidateId, string createdUtc, string rationale,
    string baseConfigPath, string baseVersion, string version,
    IReadOnlyList<FieldChange> changes, BalanceConfig config, string? path);

/// <summary>
/// Stores proposed balance changes as new files. Nothing here edits the game's balance config.
/// </summary>
/// <remarks>
/// Applying a candidate to the game is a separate, deliberate step a person takes in the game
/// repository. Keeping the two apart is what makes it possible to say which numbers produced
/// which recorded run.
/// </remarks>
public static class CandidateStore
{
    /// <summary>Balance fields a candidate may change. Anything else is rejected by name.</summary>
    public static IReadOnlyList<string> EditableFields { get; } = typeof(BalanceConfig)
        .GetFields()
        .Where(field => field.FieldType == typeof(float) || field.FieldType == typeof(int))
        .Select(field => field.Name)
        .ToArray();

    public static string Root => Path.Combine(Workspace.LabRoot, "candidates");

    public static BalanceCandidate Create(
        IReadOnlyDictionary<string, double> changes, string rationale, string? baseConfigPath = null,
        string? candidateId = null, bool write = true)
    {
        if (changes is null || changes.Count == 0)
            throw new ArgumentException("A candidate must change at least one field.");
        if (string.IsNullOrWhiteSpace(rationale))
            throw new ArgumentException("A candidate must record why it is being proposed.");

        string configPath = ConfigStore.ResolvePath(baseConfigPath);
        BalanceConfig baseConfig = ConfigStore.Load(configPath);
        BalanceConfig candidate = baseConfig.Copy();

        var applied = new List<FieldChange>();
        foreach ((string name, double value) in changes)
        {
            System.Reflection.FieldInfo field = typeof(BalanceConfig).GetField(name)
                ?? throw new ArgumentException(
                    $"Unknown balance field '{name}'. Editable fields: {string.Join(", ", EditableFields)}.");
            if (field.FieldType != typeof(float) && field.FieldType != typeof(int))
                throw new ArgumentException($"Field '{name}' is not a numeric balance value.");
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException($"Field '{name}' must be a finite number.");

            double before = Convert.ToDouble(field.GetValue(baseConfig), CultureInfo.InvariantCulture);
            if (field.FieldType == typeof(int))
            {
                if (Math.Abs(value - Math.Round(value)) > 0.0001)
                    throw new ArgumentException($"Field '{name}' is a whole number; got {value}.");
                field.SetValue(candidate, (int)Math.Round(value));
            }
            else
            {
                field.SetValue(candidate, (float)value);
            }
            applied.Add(new FieldChange(name, before, value));
        }

        // Rejects the candidate before it is stored, rather than at run time.
        candidate.Validate();

        string id = Sanitize(candidateId) ?? "cand-"
            + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..6];
        candidate.version = baseConfig.version + "+" + id;

        var result = new BalanceCandidate("1", id,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), rationale,
            configPath, baseConfig.version, candidate.version, applied, candidate, null);

        if (!write) return result;

        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, id + ".json");
        if (File.Exists(path))
            throw new ArgumentException($"Candidate '{id}' already exists at {path}. Candidates are never overwritten.");
        result = result with { path = path };
        File.WriteAllText(path, JsonSerializer.Serialize(result, ConfigStore.PrettyJson));
        return result;
    }

    public static IReadOnlyList<BalanceCandidate> List()
    {
        if (!Directory.Exists(Root)) return Array.Empty<BalanceCandidate>();
        var candidates = new List<BalanceCandidate>();
        foreach (string path in Directory.EnumerateFiles(Root, "*.json"))
        {
            BalanceCandidate? candidate = TryLoad(path);
            if (candidate is not null) candidates.Add(candidate with { path = path });
        }
        return candidates.OrderByDescending(candidate => candidate.createdUtc).ToArray();
    }

    public static BalanceCandidate Load(string idOrPath)
    {
        string path = File.Exists(idOrPath)
            ? Path.GetFullPath(idOrPath)
            : Path.Combine(Root, Sanitize(idOrPath) + ".json");
        if (!File.Exists(path))
            throw new ArgumentException(
                $"No candidate '{idOrPath}' under {Root}. Use list_balance_candidates to see what exists.");
        BalanceCandidate? candidate = TryLoad(path);
        return candidate is null
            ? throw new ArgumentException($"Not a valid candidate file: {path}")
            : candidate with { path = path };
    }

    /// <summary>Writes a candidate's config to a standalone file a simulation can load.</summary>
    public static string WriteConfigFile(BalanceCandidate candidate, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, candidate.candidateId + "-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(candidate.config, ConfigStore.PrettyJson));
        return path;
    }

    private static string? Sanitize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Length > 64 || id.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Candidate id may only contain letters, digits, '-' and '_', up to 64 characters.");
        return id;
    }

    private static BalanceCandidate? TryLoad(string path)
    {
        try
        {
            BalanceCandidate? candidate =
                JsonSerializer.Deserialize<BalanceCandidate>(File.ReadAllText(path), ConfigStore.Json);
            return candidate is null || string.IsNullOrWhiteSpace(candidate.candidateId) ? null : candidate;
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
