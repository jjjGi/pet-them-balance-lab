using System.Globalization;
using System.Text;
using PetThem.Combat;

namespace PetThem.BalanceLab;

public sealed class ReportRequest
{
    public string Title { get; init; } = "PET THEM! 밸런스 보고서";
    public required string BaselineExperimentId { get; init; }
    public IReadOnlyList<string> CandidateExperimentIds { get; init; } = Array.Empty<string>();
    /// <summary>The question this report is meant to answer, in the requester's own words.</summary>
    public string Question { get; init; } = "";
    /// <summary>File name or relative path under the reports directory.</summary>
    public string? OutputPath { get; init; }
}

public sealed record ReportResult(
    string path, string title, string createdUtc, IReadOnlyList<string> experiments,
    IReadOnlyList<string> findings, IReadOnlyList<string> missingData, long bytes);

/// <summary>
/// Builds a self-contained HTML balance report: no CDN, no server, no external font.
/// </summary>
/// <remarks>
/// Every number on the page is computed here from recorded runs. The report states which
/// experiment and which seed each figure came from, and lists what the data cannot answer
/// rather than filling the gap with a plausible sentence.
/// </remarks>
public static class ReportGenerator
{
    public static ReportResult Create(ReportRequest request)
    {
        SimulationSummary baseline = ExperimentStore.Load(request.BaselineExperimentId);
        var candidates = request.CandidateExperimentIds.Select(ExperimentStore.Load).ToArray();
        var comparisons = candidates.Select(candidate => ExperimentStore.Compare(baseline, candidate)).ToArray();

        var timelines = new Dictionary<string, IReadOnlyList<RunTimeline>>(StringComparer.Ordinal);
        foreach (SimulationSummary summary in candidates.Prepend(baseline))
            timelines[summary.experimentId] = Timelines(summary);

        string[] findings = Findings(baseline, timelines[baseline.experimentId], comparisons);
        string[] missing = MissingData(baseline, candidates);

        string html = Render(request, baseline, candidates, comparisons, timelines, findings, missing);

        string path = PathGuard.ResolveOutputDirectory(
            Path.GetDirectoryName(request.OutputPath) is { Length: > 0 } directory ? directory : null,
            Workspace.ReportsRoot);
        Directory.CreateDirectory(path);
        string file = Path.Combine(path, FileName(request));
        File.WriteAllText(file, html, new UTF8Encoding(false));

        return new ReportResult(file, request.Title,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            candidates.Select(candidate => candidate.experimentId).Prepend(baseline.experimentId).ToArray(),
            findings, missing, new FileInfo(file).Length);
    }

    private static string FileName(ReportRequest request)
    {
        string name = Path.GetFileName(request.OutputPath ?? "");
        if (string.IsNullOrWhiteSpace(name))
            name = "balance-report-"
                + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".html";
        if (!name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) name += ".html";
        if (name.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("Report file name may only contain letters, digits, '-', '_' and '.'.");
        return name;
    }

    private static IReadOnlyList<RunTimeline> Timelines(SimulationSummary summary)
    {
        if (summary.summaryPath is null) return Array.Empty<RunTimeline>();
        string? directory = Path.GetDirectoryName(summary.summaryPath);
        if (directory is null) return Array.Empty<RunTimeline>();

        var timelines = new List<RunTimeline>();
        foreach (RunResult result in summary.results)
        {
            if (result.log is null) continue;
            string path = Path.Combine(directory, result.log);
            if (!File.Exists(path)) continue;
            timelines.Add(RunLogReader.ReadTimeline(path, result.seed));
        }
        return timelines;
    }

    /// <summary>Statements that follow directly from the recorded numbers. No hypothesis, no advice.</summary>
    private static string[] Findings(
        SimulationSummary baseline, IReadOnlyList<RunTimeline> timelines,
        IReadOnlyList<ComparisonResult> comparisons)
    {
        var findings = new List<string>();
        SimulationAggregates aggregates = baseline.aggregates;

        findings.Add($"{baseline.results.Count}회 실행 중 {aggregates.winRate * 100:0.#}%가 승리로 끝났습니다 "
            + $"(정책 {baseline.policy}, 설정 {baseline.configVersion}).");

        if (aggregates.runsWithoutDamage == baseline.results.Count)
            findings.Add($"모든 실행에서 봇이 피해를 전혀 받지 않았습니다. "
                + $"체력이 {baseline.config.playerHealth:0.#}에서 한 번도 줄지 않았습니다. "
                + "현재 설정에는 이 봇이 감당하지 못하는 위협이 없습니다.");
        else
            findings.Add($"{baseline.results.Count}회 중 {aggregates.runsWithoutDamage}회는 피해 없이, "
                + $"나머지는 평균 {aggregates.meanDamageTaken:0.#}의 피해를 받았습니다.");

        findings.Add($"처치 수는 {aggregates.minKills}~{aggregates.maxKills}이고 평균 {aggregates.meanKills:0.#}입니다. "
            + $"시드에 따른 폭이 {aggregates.maxKills - aggregates.minKills}입니다.");

        if (timelines.Count > 0)
        {
            var last = timelines
                .Where(timeline => timeline.points.Count > 0)
                .Select(timeline => timeline.points[^1])
                .ToArray();
            if (last.Length > 0)
            {
                double meanAliveEnd = last.Average(point => (double)point.alive);
                double meanAliveStart = timelines
                    .Where(timeline => timeline.points.Count > 0)
                    .Average(timeline => timeline.points
                        .Where(point => point.time <= 30).Select(point => (double)point.alive)
                        .DefaultIfEmpty(0).Average());
                findings.Add($"살아 있는 적 수는 첫 30초 평균 {meanAliveStart:0.#}에서 "
                    + $"마지막 시점 평균 {meanAliveEnd:0.#}로 " + (meanAliveEnd > meanAliveStart ? "늘었습니다." : "줄거나 유지됐습니다.")
                    + " 적이 쌓이는지 보는 지표입니다.");
            }
        }

        foreach (ComparisonResult comparison in comparisons)
        {
            if (!comparison.comparable)
            {
                findings.Add($"{comparison.candidateId}는 기준과 비교할 수 없습니다: "
                    + string.Join(" ", comparison.blockers));
                continue;
            }
            MetricDelta? kills = comparison.metrics.FirstOrDefault(metric => metric.metric == "kills");
            MetricDelta? damage = comparison.metrics.FirstOrDefault(metric => metric.metric == "damageTaken");
            if (kills is not null && damage is not null)
                findings.Add($"{comparison.candidateId}는 공유 시드 {comparison.sharedSeeds.Count}개에서 "
                    + $"처치 {kills.meanDelta:+0.#;-0.#;0}, 받은 피해 {damage.meanDelta:+0.#;-0.#;0} 변화를 보였습니다.");
        }

        return findings.ToArray();
    }

    /// <summary>What this report cannot answer, stated up front so it is not read as an omission.</summary>
    private static string[] MissingData(SimulationSummary baseline, IReadOnlyList<SimulationSummary> candidates)
    {
        var missing = new List<string>
        {
            "사람의 플레이 기록이 0건입니다. 실제 플레이어가 어디서 막히는지는 이 데이터로 알 수 없습니다.",
            "이 보고서의 모든 수치는 봇 시뮬레이션입니다. 반응 속도, 조준 정확도, 손의 피로를 흉내 내지 않습니다.",
            "무기는 펀치 하나뿐이고 펫도 1종입니다. 무기·펫 조합 분석은 대상이 없습니다.",
            "레벨업 선택지, 경험치, 상점, 재화가 아직 구현되지 않았습니다. 성장 정체 분석은 불가능합니다.",
            "보스가 없습니다. 보스전 관련 지표는 없습니다.",
        };

        if (candidates.Count == 0)
            missing.Add("비교 대상 실험이 지정되지 않았습니다. 변경 전후 비교 결과가 없습니다.");
        if (baseline.results.Count < 20)
            missing.Add($"기준 실험의 표본이 {baseline.results.Count}회입니다. "
                + "시드별 변동을 구분하기에는 적습니다. 방향만 참고하세요.");
        return missing.ToArray();
    }

    private static string Render(
        ReportRequest request, SimulationSummary baseline, IReadOnlyList<SimulationSummary> candidates,
        IReadOnlyList<ComparisonResult> comparisons,
        IReadOnlyDictionary<string, IReadOnlyList<RunTimeline>> timelines,
        IReadOnlyList<string> findings, IReadOnlyList<string> missing)
    {
        string E(string? text) => Svg.Escape(text);
        var page = new StringBuilder();

        page.Append("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\">");
        page.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        page.Append($"<title>{E(request.Title)}</title>");
        page.Append("<style>").Append(Css).Append("</style></head><body>");

        page.Append("<header class=\"page-head\"><h1>").Append(E(request.Title)).Append("</h1>");
        page.Append("<p class=\"meta\">생성 ").Append(E(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        page.Append(" · 기준 실험 <code>").Append(E(baseline.experimentId)).Append("</code></p>");
        if (!string.IsNullOrWhiteSpace(request.Question))
            page.Append("<p class=\"question\"><strong>질문:</strong> ").Append(E(request.Question)).Append("</p>");
        page.Append("<p class=\"banner\">이 보고서의 모든 수치는 <strong>봇 시뮬레이션</strong>에서 나왔습니다. ")
            .Append("사람이 실제로 플레이한 기록은 아직 없습니다.</p>");
        page.Append("</header><main>");

        page.Append("<section id=\"summary\"><h2>핵심 요약</h2><ol class=\"findings\">");
        foreach (string finding in findings) page.Append("<li>").Append(E(finding)).Append("</li>");
        page.Append("</ol><p class=\"note\">위 문장은 기록된 실행에서 계산한 것입니다. 원인 가설이 아닙니다.</p></section>");

        page.Append("<section id=\"unknown\"><h2>이 보고서가 답할 수 없는 것</h2><ul class=\"missing\">");
        foreach (string item in missing) page.Append("<li>").Append(E(item)).Append("</li>");
        page.Append("</ul></section>");

        page.Append("<section id=\"data\"><h2>사용한 데이터</h2>");
        page.Append("<table><caption>이 보고서에 들어간 실험</caption><thead><tr>");
        page.Append("<th>역할</th><th>실험 ID</th><th>정책</th><th>설정 버전</th><th>실행</th><th>시드</th><th>설명</th>");
        page.Append("</tr></thead><tbody>");
        AppendExperimentRow(page, "기준", baseline);
        foreach (SimulationSummary candidate in candidates) AppendExperimentRow(page, "비교", candidate);
        page.Append("</tbody></table>");
        page.Append("<p class=\"note\">").Append(E(baseline.limitation)).Append("</p></section>");

        page.Append("<section id=\"runs\"><h2>실행별 결과</h2>");
        page.Append("<table><caption>기준 실험 ").Append(E(baseline.experimentId)).Append("</caption><thead><tr>");
        page.Append("<th>시드</th><th>결과</th><th>생존(초)</th><th>처치</th><th>남은 체력</th><th>받은 피해</th><th>등장한 적</th>");
        page.Append("</tr></thead><tbody>");
        foreach (RunResult result in baseline.results)
        {
            page.Append("<tr><td>").Append(result.seed).Append("</td><td>").Append(E(result.state));
            page.Append("</td><td>").Append(Number(result.seconds, 1));
            page.Append("</td><td>").Append(result.kills);
            page.Append("</td><td>").Append(Number(result.health, 1));
            page.Append("</td><td>").Append(Number(result.damageTaken, 1));
            page.Append("</td><td>").Append(result.enemiesSpawned).Append("</td></tr>");
        }
        page.Append("</tbody></table></section>");

        IReadOnlyList<RunTimeline> baselineTimelines = timelines[baseline.experimentId];
        page.Append("<section id=\"time\"><h2>시간에 따른 변화</h2>");
        if (baselineTimelines.Count == 0)
        {
            page.Append("<p class=\"empty\">기록 파일을 찾지 못해 시계열을 그릴 수 없습니다. ")
                .Append("<code>writeLogs</code>를 켜고 실험을 다시 실행하세요.</p>");
        }
        else
        {
            page.Append("<p>각 선은 시드 하나입니다. 1초마다 기록된 스냅샷만 사용하며 그 사이를 추정하지 않습니다.</p>");
            page.Append("<figure>").Append(Svg.LineChart("살아 있는 적 수", "시간(초)", "적 수",
                baselineTimelines.Select(timeline => new ChartSeries($"시드 {timeline.seed}",
                    timeline.points.Select(point => (point.time, (double)point.alive)).ToArray())).ToArray(),
                yMinimum: 0));
            page.Append("<figcaption>선이 오른쪽으로 갈수록 올라가면 처치 속도가 등장 속도를 못 따라가는 것입니다.</figcaption></figure>");

            page.Append("<figure>").Append(Svg.LineChart("남은 체력", "시간(초)", "체력",
                baselineTimelines.Select(timeline => new ChartSeries($"시드 {timeline.seed}",
                    timeline.points.Select(point => (point.time, point.health)).ToArray())).ToArray(),
                yMinimum: 0, yMaximum: baseline.config.playerHealth));
            page.Append("<figcaption>평평한 직선은 그 실행에서 한 번도 맞지 않았다는 뜻입니다.</figcaption></figure>");

            page.Append("<figure>").Append(Svg.LineChart("누적 처치 수", "시간(초)", "처치",
                baselineTimelines.Select(timeline => new ChartSeries($"시드 {timeline.seed}",
                    timeline.points.Select(point => (point.time, point.kills)).ToArray())).ToArray(),
                yMinimum: 0));
            page.Append("<figcaption>기울기가 처치 속도입니다.</figcaption></figure>");
        }
        page.Append("</section>");

        if (comparisons.Count > 0)
        {
            page.Append("<section id=\"compare\"><h2>비교 실험</h2>");
            foreach (ComparisonResult comparison in comparisons) AppendComparison(page, comparison);
            page.Append("</section>");
        }

        page.Append("<section id=\"config\"><h2>기준 설정 값</h2>");
        page.Append("<p class=\"note\">이 값들은 규칙 데이터입니다. 원인이나 문제점이 아닙니다.</p>");
        page.Append("<table><caption>").Append(E(baseline.configVersion)).Append("</caption>");
        page.Append("<thead><tr><th>항목</th><th>값</th></tr></thead><tbody>");
        foreach (System.Reflection.FieldInfo field in typeof(BalanceConfig).GetFields())
            page.Append("<tr><td><code>").Append(E(field.Name)).Append("</code></td><td>")
                .Append(E(Convert.ToString(field.GetValue(baseline.config), CultureInfo.InvariantCulture)))
                .Append("</td></tr>");
        page.Append("</tbody></table><p class=\"note\">설정 원본: <code>")
            .Append(E(baseline.configPath)).Append("</code></p></section>");

        page.Append("</main><footer><p>PET THEM! Balance Lab · 이 파일은 외부 서버나 인터넷 연결 없이 열립니다.</p></footer>");
        page.Append("</body></html>");
        return page.ToString();

        void AppendExperimentRow(StringBuilder builder, string role, SimulationSummary summary)
        {
            builder.Append("<tr><td>").Append(E(role)).Append("</td><td><code>").Append(E(summary.experimentId));
            builder.Append("</code></td><td>").Append(E(summary.policy));
            builder.Append("</td><td>").Append(E(summary.configVersion));
            builder.Append("</td><td>").Append(summary.results.Count);
            builder.Append("</td><td>").Append(E(string.Join(", ", summary.Seeds)));
            builder.Append("</td><td>").Append(E(string.IsNullOrWhiteSpace(summary.label) ? "—" : summary.label));
            builder.Append("</td></tr>");
        }

        void AppendComparison(StringBuilder builder, ComparisonResult comparison)
        {
            builder.Append("<article class=\"comparison\"><h3><code>").Append(E(comparison.candidateId))
                .Append("</code> vs 기준</h3>");

            if (comparison.configDifferences.Count > 0)
            {
                builder.Append("<p><strong>설정 차이</strong></p><ul class=\"diff\">");
                foreach (string difference in comparison.configDifferences)
                    builder.Append("<li><code>").Append(E(difference)).Append("</code></li>");
                builder.Append("</ul>");
            }

            if (!comparison.comparable)
            {
                builder.Append("<p class=\"blocked\"><strong>비교할 수 없습니다.</strong></p><ul class=\"missing\">");
                foreach (string blocker in comparison.blockers) builder.Append("<li>").Append(E(blocker)).Append("</li>");
                builder.Append("</ul></article>");
                return;
            }

            foreach (string warning in comparison.warnings)
                builder.Append("<p class=\"warn\">").Append(E(warning)).Append("</p>");

            builder.Append("<table><caption>공유 시드 ").Append(comparison.sharedSeeds.Count)
                .Append("개에서 시드끼리 짝지어 계산</caption><thead><tr>");
            builder.Append("<th>지표</th><th>기준 평균</th><th>후보 평균</th><th>평균 변화</th>");
            builder.Append("<th>최소~최대 변화</th><th title=\"좋고 나쁨이 아니라 방향만 셉니다\">증가 / 감소 / 동일</th></tr></thead><tbody>");
            foreach (MetricDelta metric in comparison.metrics)
            {
                builder.Append("<tr><td>").Append(E(metric.metric));
                builder.Append("</td><td>").Append(Number(metric.baselineMean, 2));
                builder.Append("</td><td>").Append(Number(metric.candidateMean, 2));
                builder.Append("</td><td class=\"delta\">").Append(Signed(metric.meanDelta));
                builder.Append("</td><td>").Append(Signed(metric.minDelta)).Append(" ~ ").Append(Signed(metric.maxDelta));
                builder.Append("</td><td>").Append(metric.seedsIncreased).Append(" / ")
                    .Append(metric.seedsDecreased).Append(" / ").Append(metric.seedsUnchanged);
                builder.Append("</td></tr>");
            }
            builder.Append("</tbody></table>");
            builder.Append("<p class=\"note\">").Append(E(comparison.interpretation)).Append("</p></article>");
        }
    }

    private static string Number(double value, int digits) =>
        value.ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);

    private static string Signed(double value) =>
        value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);

    private const string Css = """
        :root {
          color-scheme: light dark;
          --bg: #ffffff; --fg: #1a1c1f; --muted: #5b6169; --line: #d9dde3;
          --card: #f6f7f9; --accent: #1f5fd0; --warn-bg: #fff6e0; --warn-fg: #7a5200;
          --miss-bg: #f2f4f7; --s0: #1f5fd0; --s1: #b0451f; --s2: #1d7a4f; --s3: #7a3fb0; --s4: #9a7000;
        }
        @media (prefers-color-scheme: dark) {
          :root:not([data-theme="light"]) {
            --bg: #14161a; --fg: #e7e9ec; --muted: #a2a9b3; --line: #2c313a;
            --card: #1b1e24; --accent: #6ba2ff; --warn-bg: #3a2f14; --warn-fg: #ffd98a;
            --miss-bg: #1b1e24; --s0: #6ba2ff; --s1: #ff9166; --s2: #58cf96; --s3: #c79bf0; --s4: #e0bb4a;
          }
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; padding: 0 16px 64px; background: var(--bg); color: var(--fg);
          font: 16px/1.65 system-ui, -apple-system, "Segoe UI", "Malgun Gothic", sans-serif;
        }
        main, .page-head, footer { max-width: 880px; margin: 0 auto; }
        .page-head { padding: 32px 0 8px; border-bottom: 1px solid var(--line); }
        h1 { font-size: 1.75rem; margin: 0 0 8px; }
        h2 { font-size: 1.25rem; margin: 40px 0 12px; }
        h3 { font-size: 1.05rem; margin: 24px 0 8px; }
        .meta { color: var(--muted); margin: 0 0 12px; font-size: .9rem; }
        .question { background: var(--card); border-left: 3px solid var(--accent); padding: 10px 14px; margin: 12px 0; }
        .banner { background: var(--warn-bg); color: var(--warn-fg); padding: 10px 14px; border-radius: 6px; }
        section { padding-top: 8px; }
        .findings li { margin-bottom: 10px; }
        .missing { background: var(--miss-bg); border-radius: 6px; padding: 14px 14px 14px 32px; }
        .missing li { margin-bottom: 8px; }
        .note { color: var(--muted); font-size: .9rem; }
        .warn { background: var(--warn-bg); color: var(--warn-fg); padding: 8px 12px; border-radius: 6px; }
        .blocked { color: var(--s1); font-weight: 600; }
        .empty { color: var(--muted); font-style: italic; }
        table { border-collapse: collapse; width: 100%; margin: 12px 0; font-size: .92rem; }
        caption { text-align: left; color: var(--muted); padding-bottom: 6px; font-size: .88rem; }
        th, td { border-bottom: 1px solid var(--line); padding: 7px 10px; text-align: left; }
        th { font-weight: 600; background: var(--card); }
        td.delta { font-variant-numeric: tabular-nums; font-weight: 600; }
        code { background: var(--card); padding: 1px 5px; border-radius: 4px; font-size: .88em; word-break: break-all; }
        figure { margin: 20px 0; }
        figcaption { color: var(--muted); font-size: .88rem; margin-top: 4px; }
        .chart { width: 100%; height: auto; display: block; }
        .chart .grid { stroke: var(--line); stroke-width: 1; }
        .chart .axis { stroke: var(--muted); stroke-width: 1; }
        .chart .tick, .chart .axis-label { fill: var(--muted); font-size: 11px; }
        .chart .series { stroke-width: 1.6; }
        .chart .series.emphasis { stroke-width: 2.6; }
        .s0 { stroke: var(--s0); } .s1 { stroke: var(--s1); } .s2 { stroke: var(--s2); }
        .s3 { stroke: var(--s3); } .s4 { stroke: var(--s4); }
        .legend { list-style: none; display: flex; flex-wrap: wrap; gap: 12px; padding: 0; margin: 6px 0 0;
          font-size: .85rem; color: var(--muted); }
        .legend .swatch { display: inline-block; width: 14px; height: 3px; margin-right: 6px; vertical-align: middle; }
        .swatch.s0 { background: var(--s0); } .swatch.s1 { background: var(--s1); }
        .swatch.s2 { background: var(--s2); } .swatch.s3 { background: var(--s3); }
        .swatch.s4 { background: var(--s4); }
        .comparison { border: 1px solid var(--line); border-radius: 8px; padding: 4px 16px 16px; margin: 20px 0; }
        .diff li { margin-bottom: 4px; }
        footer { color: var(--muted); font-size: .85rem; border-top: 1px solid var(--line); padding-top: 16px; margin-top: 48px; }
        @media print {
          body { padding: 0; }
          .comparison, table { break-inside: avoid; }
          .banner, .missing { border: 1px solid #999; }
        }
        @media (max-width: 640px) {
          table { display: block; overflow-x: auto; }
        }
        """;
}
