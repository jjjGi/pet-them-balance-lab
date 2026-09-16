using System.Globalization;
using System.Text;

namespace PetThem.BalanceLab;

internal static class PlaytestReport
{
    private static string E(string text) => Svg.Escape(text);
    private static string N(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—";

    public static string Render(ReportRequest request, PlaytestAnalysis analysis)
    {
        var page = new StringBuilder("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        page.Append($"<title>{E(request.Title)}</title><style>{ReportGenerator.Css}</style></head><body>");
        page.Append($"<header class=\"page-head\"><p class=\"meta\">PET THEM! · PLAYTEST REVIEW</p><h1>{E(request.Title)}</h1>");
        page.Append($"<p class=\"question\">{E(request.Question)}</p><p class=\"banner\">선택한 클라이언트 기록 {analysis.suppliedFiles}개 중 {analysis.includedRuns}회 포함 · {analysis.excluded.Count}개 제외. 봇 통계와 합치지 않았습니다.</p></header><main>");
        page.Append("<section><h2>어디부터 살펴볼까?</h2><ol class=\"findings\">");
        foreach (var group in analysis.groups)
        {
            var peak = group.intervals.Where(i => i.damagePerObservedMinute.HasValue)
                .OrderByDescending(i => i.damagePerObservedMinute).FirstOrDefault();
            page.Append($"<li><strong>{E(group.id)}</strong> · {group.runs.Count}회 중 사망 {group.runs.Count(r => r.outcome == "death")}회, 생존 완료 {group.runs.Count(r => r.outcome == "survived")}회, 중도 종료 {group.runs.Count(r => r.outcome == "abandoned")}회, 불완전 {group.runs.Count(r => r.outcome == "incomplete")}회.");
            if (peak is not null && peak.damageTaken > 0)
                page.Append($" 피해/분이 가장 큰 구간은 <strong>{N(peak.startSeconds)}–{N(peak.endSeconds)}초</strong>입니다. 도달 {peak.reached}회, 관측 시간 합계 {N(peak.exposureSeconds)}초, 받은 피해 {N(peak.damageTaken)}, 환산 피해/분 {N(peak.damagePerObservedMinute)}.");
            else page.Append(" 관측된 구간의 피격이 없습니다. 쉬운 게임이라는 결론까지는 내릴 수 없습니다.");
            page.Append("</li>");
        }
        if (analysis.includedRuns == 0) page.Append("<li>분석할 유효 기록이 없습니다. 아래 제외 사유를 확인하세요.</li>");
        page.Append("</ol><div class=\"question\"><strong>다음 확인 방법</strong><p>피격이 몰린 구간의 적 수와 체력 그래프를 함께 보세요. 적이 늘면서 피해도 커졌다면 생성 속도·적 이동 속도·공격 범위가 원인 후보입니다. 적 수가 적어도 맞았다면 공격 방향과 이동 조작을 먼저 확인하세요.</p><p>이것은 원인 가설입니다. 해당 구간을 다시 플레이하고, 한 번에 한 값만 바꾼 후보를 같은 조건으로 비교한 뒤 반영하세요. 지금 기록만으로 특정 수치를 자동 변경하지 않습니다.</p></div></section>");
        page.Append("<section id=\"unknown\"><h2>판단의 범위</h2><ul class=\"missing\">");
        foreach (string limitation in analysis.limitations) page.Append($"<li>{E(limitation)}</li>");
        page.Append("</ul></section>");

        foreach (var group in analysis.groups)
        {
            page.Append($"<section><h2>{E(group.id)} · 같은 실행 조건</h2><p class=\"meta\">빌드 {E(group.buildVersion)} · {E(group.platform)} · 설정 {E(group.configVersion)} · 설정 해시 <code>{E(group.configHash)}</code> · 고정 스텝 {N(group.runs[0].fixedStep)}초</p>");
            page.Append("<h3>시간대별 피격과 종료</h3><p class=\"note\">구간은 시작 포함·끝 제외입니다. 예: 정확히 30초에 사망하면 30–60초 구간에 포함됩니다. 짧게 관측된 구간의 피해/분은 크게 흔들릴 수 있습니다.</p><table><thead><tr><th>구간(초)</th><th>도달</th><th>사망/도달</th><th>생존 완료</th><th>중도/불완전</th><th>피해/분</th><th>평균 적 수</th></tr></thead><tbody>");
            foreach (var interval in group.intervals)
                page.Append($"<tr><td>{N(interval.startSeconds)}–{N(interval.endSeconds)}</td><td>{interval.reached}</td><td>{interval.deaths}/{interval.reached} ({N(interval.deathFractionOfReached * 100)}%)</td><td>{interval.survived}</td><td>{interval.abandoned}/{interval.incomplete}</td><td>{N(interval.damagePerObservedMinute)}</td><td>{N(interval.meanAliveSnapshot)} (표본 {interval.snapshotCount})</td></tr>");
            page.Append("</tbody></table><h3>실행별 체력과 적 누적</h3><p class=\"note\">실행별 그래프는 기록된 스냅샷을 연결합니다. 선 사이의 값은 측정값이 아닙니다. 짧은 기록을 뒤 구간까지 연장하지 않습니다.</p>");
            for (int index = 0; index < group.runs.Count; index++)
            {
                var run = group.runs[index];
                var points = run.events.Where(e => e.type == "snapshot").ToArray();
                page.Append($"<details{(group.runs.Count <= 3 ? " open" : "")}><summary>실행 {index + 1} · {N(run.observedSeconds)}초 · {Outcome(run.outcome)} · 처치 {run.kills} · 받은 피해 {N(run.damageTaken)}</summary>");
                page.Append($"<p>원본 파일 <code>{E(Path.GetFileName(run.path))}</code><br>runId <code>{E(run.runId)}</code></p>");
                foreach (string warning in run.warnings) page.Append($"<p class=\"warn\">{E(warning)}</p>");
                page.Append(Svg.LineChart("플레이어 체력", "게임 시간(초)", "체력", new[] { new ChartSeries("체력", points.Select(e => (e.time, e.health)).ToArray(), true) }, yMinimum: 0));
                page.Append(Svg.LineChart("살아 있는 적", "게임 시간(초)", "적 수", new[] { new ChartSeries("적 수", points.Select(e => (e.time, (double)e.alive)).ToArray(), true) }, yMinimum: 0));
                double total = run.damageBySource.Values.Sum();
                page.Append("<table><caption>적에게 적용한 피해 기여도 · 피해량이 같아야 한다는 뜻은 아닙니다</caption><thead><tr><th>공격 주체</th><th>공격 횟수</th><th>적용 피해</th><th>비중</th></tr></thead><tbody>");
                foreach (string source in run.events.Where(e => e.type is "attack" or "damage").Select(e => e.source).Distinct())
                {
                    double damage = run.damageBySource.GetValueOrDefault(source);
                    page.Append($"<tr><td>{E(source)}</td><td>{run.events.Count(e => e.type == "attack" && e.source == source)}</td><td>{N(damage)}</td><td>{(total > 0 ? N(damage / total * 100) + "%" : "—")}</td></tr>");
                }
                page.Append("</tbody></table></details>");
            }
            page.Append("</section>");
        }
        page.Append("<section><h2>제외한 입력</h2><ul>");
        foreach (var excluded in analysis.excluded)
            page.Append($"<li><code>{E(Path.GetFileName(excluded.path))}</code>: {E(excluded.reason)}</li>");
        if (analysis.excluded.Count == 0) page.Append("<li>없음</li>");
        page.Append("</ul></section></main><footer>로컬 파일에서 계산한 보고서 · 외부 서버와 통신하지 않습니다. 원본 기록은 이 HTML에 포함하지 않습니다.</footer></body></html>");
        return page.ToString();
    }

    private static string Outcome(string outcome) => outcome switch
    {
        "survived" => "생존 완료", "death" => "사망", "abandoned" => "중도 종료", _ => "불완전 기록"
    };
}
