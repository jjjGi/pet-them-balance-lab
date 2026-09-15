using System.Diagnostics;
using System.Text.Json;
using PetThem.BalanceLab;
using PetThem.Combat;

// Checks for the balance lab. No test package needed, same style as the game repository's CoreChecks.
string scratch = Path.Combine(Path.GetTempPath(), "petthem-lab-checks-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(scratch);
int passed = 0;
try
{
    Check("output paths stay inside the experiments directory", () =>
    {
        string root = Workspace.ExperimentsRoot;
        True(PathGuard.ResolveOutputDirectory("smoke", root) == Path.Combine(root, "smoke"));
        True(PathGuard.ResolveOutputDirectory(null, root) == Path.GetFullPath(root));
        Throws(() => PathGuard.ResolveOutputDirectory("../../escape", root));
        Throws(() => PathGuard.ResolveOutputDirectory(Path.Combine(Path.GetTempPath(), "escape"), root));
        Throws(() => PathGuard.ResolveInputFile(Path.Combine(scratch, "missing.jsonl")));
    });

    Check("every registered policy runs and an unknown policy is rejected", () =>
    {
        Throws(() => BotPolicies.Get("does-not-exist"));
        True(BotPolicies.Get(null).Id == BotPolicies.Default);
        True(BotPolicies.All.Select(policy => policy.Id).Distinct().Count() == BotPolicies.All.Count);
        foreach (IBotPolicy policy in BotPolicies.All)
        {
            SimulationSummary summary = Simulate(policy.Id, runs: 1, seed: 7, duration: 12);
            True(summary.results.Count == 1);
            True(summary.results[0].state is nameof(RunState.Won) or nameof(RunState.Lost));
        }
    });

    Check("the same seed and policy reproduce the same run", () =>
    {
        SimulationSummary first = Simulate(BotPolicies.Default, runs: 3, seed: 42, duration: 20);
        SimulationSummary second = Simulate(BotPolicies.Default, runs: 3, seed: 42, duration: 20);
        True(Json(first.results) == Json(second.results));
        SimulationSummary other = Simulate(BotPolicies.Default, runs: 3, seed: 99, duration: 20);
        True(Json(first.results) != Json(other.results));
    });

    Check("aggregates match the runs they summarise", () =>
    {
        SimulationSummary summary = Simulate(BotPolicies.Default, runs: 4, seed: 42, duration: 20);
        SimulationAggregates aggregates = summary.aggregates;
        True(aggregates.runs == summary.results.Count);
        True(aggregates.minKills == summary.results.Min(r => r.kills));
        True(aggregates.maxKills == summary.results.Max(r => r.kills));
        Near(aggregates.meanKills, summary.results.Average(r => (double)r.kills));
        Near(aggregates.winRate,
            summary.results.Count(r => r.state == nameof(RunState.Won)) / (double)summary.results.Count);
        True(aggregates.runsWithoutDamage == summary.results.Count(r => r.damageTaken <= 0));
    });

    Check("run count and duration limits are enforced", () =>
    {
        Throws(() => Simulate(BotPolicies.Default, runs: 0, seed: 1, duration: 10));
        Throws(() => Simulate(BotPolicies.Default, runs: SimulationRunner.MaxRuns + 1, seed: 1, duration: 10));
        Throws(() => Simulate(BotPolicies.Default, runs: 1, seed: 1,
            duration: SimulationRunner.MaxDurationSeconds + 1));
    });

    Check("a written run log reports the same totals the runner returned", () =>
    {
        string output = Path.Combine(scratch, "logged");
        SimulationSummary summary = Simulate(BotPolicies.Default, runs: 2, seed: 42, duration: 30,
            output: output, writeLogs: true);
        True(summary.summaryPath is not null && File.Exists(summary.summaryPath));
        foreach (RunResult result in summary.results)
        {
            True(result.log is not null);
            RunLogSummary log = RunLogReader.Read(Path.Combine(output, result.log!));
            True(log.complete);
            True(log.warnings.Count == 0);
            True(log.killEvents == result.kills);
            True(log.endKills == result.kills);
            True(log.seed == result.seed);
            True(log.policy == BotPolicies.Default);
            True(log.source == "simulation");
            True(log.configVersion == summary.config.version);
            Near(log.damageTaken, result.damageTaken);
            True(log.eventCounts.GetValueOrDefault("snapshot") == (int)Math.Round(summary.config.duration));
            True(log.eventCounts.GetValueOrDefault("run_end") == 1);
        }
    });

    Check("an incomplete log is flagged instead of being read as a death", () =>
    {
        string output = Path.Combine(scratch, "truncated");
        SimulationSummary summary = Simulate(BotPolicies.Default, runs: 1, seed: 42, duration: 15,
            output: output, writeLogs: true);
        string path = Path.Combine(output, summary.results[0].log!);
        string[] lines = File.ReadAllLines(path);
        File.WriteAllLines(path, lines.Take(lines.Length / 2));
        RunLogSummary log = RunLogReader.Read(path);
        True(!log.complete);
        True(log.endReason is null);
        True(log.warnings.Any(warning => warning.Contains("incomplete")));

        string headerless = Path.Combine(scratch, "headerless.jsonl");
        File.WriteAllLines(headerless, new[] { "{\"type\":\"snapshot\",\"value\":1}", "not json" });
        RunLogSummary broken = RunLogReader.Read(headerless);
        True(!broken.complete);
        True(broken.warnings.Count >= 2);
    });

    Check("a corrupt or empty balance config is rejected", () =>
    {
        string path = Path.Combine(scratch, "bad.json");
        File.WriteAllText(path, "{\"punchCooldown\":0}");
        Throws(() => ConfigStore.Load(path));
        File.WriteAllText(path, "null");
        Throws(() => ConfigStore.Load(path));
        BalanceConfig config = ConfigStore.LoadDefault();
        True(!string.IsNullOrWhiteSpace(config.version));
    });

    Check("the MCP server answers initialize, tools/list and tools/call over stdio", () =>
    {
        string[] requests =
        {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"lab-checks\",\"version\":\"0.1.0\"}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_lab_status\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"run_simulation\",\"arguments\":{\"runs\":1,\"seed\":42,\"writeLogs\":false}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"run_simulation\",\"arguments\":{\"runs\":1,\"seed\":42,\"outputDirectory\":\"../../escape\"}}}",
        };
        Dictionary<int, JsonElement> responses = Handshake(McpServerExecutable(), requests);

        JsonElement initialize = responses[1].GetProperty("result");
        True(initialize.GetProperty("serverInfo").GetProperty("name").GetString() == "pet-them-balance-lab");

        string[] tools = responses[2].GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(tool => tool.GetProperty("name").GetString() ?? "").ToArray();
        foreach (string expected in new[]
                 { "get_lab_status", "get_balance_config", "list_bot_policies", "run_simulation", "read_run_log" })
            True(tools.Contains(expected));

        JsonElement status = ToolPayload(responses[3]);
        True(status.GetProperty("missingPaths").GetArrayLength() == 0);

        JsonElement simulation = ToolPayload(responses[4]);
        True(simulation.GetProperty("results").GetArrayLength() == 1);
        True(simulation.GetProperty("results")[0].GetProperty("state").GetString() == nameof(RunState.Won));
        True(simulation.GetProperty("summaryPath").ValueKind == JsonValueKind.Null);

        JsonElement rejected = ToolPayload(responses[5]);
        True(rejected.GetProperty("error").GetString() == nameof(ArgumentException));
    });

    Console.WriteLine(passed + " checks passed.");
}
finally
{
    try { Directory.Delete(scratch, true); } catch (IOException) { /* leave the copy behind for inspection */ }
}
return;

SimulationSummary Simulate(string policy, int runs, int seed, float duration,
    string? output = null, bool writeLogs = false)
{
    BalanceConfig config = ConfigStore.LoadDefault();
    config.duration = duration;
    return SimulationRunner.Run(new SimulationRequest
    {
        Config = config,
        ConfigPath = Workspace.DefaultConfigPath,
        PolicyId = policy,
        Runs = runs,
        Seed = seed,
        OutputDirectory = output ?? "",
        WriteLogs = writeLogs,
    });
}

string McpServerExecutable()
{
    string root = Path.Combine(Workspace.LabRoot, "src", "McpServer", "bin");
    string name = OperatingSystem.IsWindows() ? "PetThem.BalanceLab.Mcp.exe" : "PetThem.BalanceLab.Mcp";
    string? found = Directory.Exists(root)
        ? Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;
    return found ?? throw new Exception(
        $"MCP server executable not found under {root}. Build it first: dotnet build src/McpServer -c Release");
}

Dictionary<int, JsonElement> Handshake(string exe, string[] requests)
{
    var startInfo = new ProcessStartInfo(exe)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using Process process = Process.Start(startInfo)
        ?? throw new Exception("Could not start the MCP server.");

    int expected = requests.Count(request => request.Contains("\"id\":"));
    var responses = new Dictionary<int, JsonElement>();
    using var complete = new ManualResetEventSlim(false);
    Task reader = Task.Run(() =>
    {
        while (process.StandardOutput.ReadLine() is { } line)
        {
            JsonElement message = JsonDocument.Parse(line).RootElement.Clone();
            if (!message.TryGetProperty("id", out JsonElement id) || !id.TryGetInt32(out int number)) continue;
            lock (responses)
            {
                responses[number] = message;
                if (responses.Count >= expected) complete.Set();
            }
        }
        complete.Set();
    });
    Task<string> stderr = Task.Run(() => process.StandardError.ReadToEnd());

    foreach (string request in requests) process.StandardInput.WriteLine(request);
    process.StandardInput.Flush();

    // A real client keeps stdin open while it waits. Closing it early shuts the transport down
    // before the server has written its replies.
    bool answered = complete.Wait(120_000);
    process.StandardInput.Close();
    if (!process.WaitForExit(30_000))
    {
        process.Kill(true);
        throw new Exception("The MCP server did not exit after stdin closed.");
    }
    reader.Wait(30_000);

    lock (responses)
    {
        if (!answered || responses.Count != expected)
            throw new Exception($"Expected {expected} responses but got {responses.Count}. stderr: {stderr.Result}");
        return new Dictionary<int, JsonElement>(responses);
    }
}

JsonElement ToolPayload(JsonElement response)
{
    JsonElement result = response.GetProperty("result");
    if (result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean())
        throw new Exception("Tool call reported an error: " + result);
    string text = result.GetProperty("content")[0].GetProperty("text").GetString()
        ?? throw new Exception("Tool call returned no text content.");
    return JsonDocument.Parse(text).RootElement.Clone();
}

string Json(object value) => JsonSerializer.Serialize(value, ConfigStore.Json);
void Check(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
void Near(double a, double b) { if (Math.Abs(a - b) > 0.0001) throw new Exception(a + " != " + b); }

void Throws(Action action)
{
    try { action(); }
    catch (ArgumentException) { return; }
    catch (FileNotFoundException) { return; }
    throw new Exception("Expected the call to be rejected.");
}
