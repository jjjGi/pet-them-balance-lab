using System.Globalization;
using System.Text;

namespace PetThem.BalanceLab;

public sealed record ChartSeries(string name, IReadOnlyList<(double x, double y)> points, bool emphasis = false);

/// <summary>
/// Draws small line charts as inline SVG so a report stays a single file with no script library.
/// </summary>
/// <remarks>
/// Series are distinguished by label and dash pattern as well as colour, so the chart still reads
/// in greyscale or for a viewer who cannot separate the hues.
/// </remarks>
public static class Svg
{
    private static readonly string[] Dashes = { "none", "6 3", "2 3", "8 3 2 3", "4 2" };

    public static string LineChart(
        string title, string xLabel, string yLabel, IReadOnlyList<ChartSeries> series,
        double? yMinimum = null, double? yMaximum = null, int width = 720, int height = 300)
    {
        const int left = 58, right = 16, top = 16, bottom = 44;
        double plotWidth = width - left - right;
        double plotHeight = height - top - bottom;

        var withPoints = series.Where(item => item.points.Count > 0).ToArray();
        if (withPoints.Length == 0)
            return $"<p class=\"empty\">{Escape(title)}: 그릴 데이터가 없습니다.</p>";

        double xMin = withPoints.Min(item => item.points.Min(point => point.x));
        double xMax = withPoints.Max(item => item.points.Max(point => point.x));
        double yMin = yMinimum ?? withPoints.Min(item => item.points.Min(point => point.y));
        double yMax = yMaximum ?? withPoints.Max(item => item.points.Max(point => point.y));
        if (Math.Abs(xMax - xMin) < 1e-9) xMax = xMin + 1;
        if (Math.Abs(yMax - yMin) < 1e-9) yMax = yMin + 1;
        // Leave headroom so a flat line at the maximum is still visible inside the plot.
        yMax += (yMax - yMin) * 0.08;

        double ScaleX(double x) => left + (x - xMin) / (xMax - xMin) * plotWidth;
        double ScaleY(double y) => top + plotHeight - (y - yMin) / (yMax - yMin) * plotHeight;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg class=\"chart\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"{Escape(title)}\">");
        svg.Append($"<title>{Escape(title)}</title>");

        for (int tick = 0; tick <= 4; tick++)
        {
            double value = yMin + (yMax - yMin) * tick / 4;
            double y = ScaleY(value);
            svg.Append(Format($"<line class=\"grid\" x1=\"{left}\" y1=\"{y:0.##}\" x2=\"{left + plotWidth}\" y2=\"{y:0.##}\"/>"));
            svg.Append(Format($"<text class=\"tick\" x=\"{left - 8}\" y=\"{y + 4:0.##}\" text-anchor=\"end\">{value:0.#}</text>"));
        }
        for (int tick = 0; tick <= 4; tick++)
        {
            double value = xMin + (xMax - xMin) * tick / 4;
            double x = ScaleX(value);
            svg.Append(Format($"<text class=\"tick\" x=\"{x:0.##}\" y=\"{top + plotHeight + 18}\" text-anchor=\"middle\">{value:0.#}</text>"));
        }

        svg.Append(Format($"<line class=\"axis\" x1=\"{left}\" y1=\"{top}\" x2=\"{left}\" y2=\"{top + plotHeight}\"/>"));
        svg.Append(Format($"<line class=\"axis\" x1=\"{left}\" y1=\"{top + plotHeight}\" x2=\"{left + plotWidth}\" y2=\"{top + plotHeight}\"/>"));
        svg.Append(Format($"<text class=\"axis-label\" x=\"{left + plotWidth / 2}\" y=\"{height - 6}\" text-anchor=\"middle\">{Escape(xLabel)}</text>"));
        svg.Append(Format($"<text class=\"axis-label\" x=\"14\" y=\"{top + plotHeight / 2}\" text-anchor=\"middle\" transform=\"rotate(-90 14 {top + plotHeight / 2})\">{Escape(yLabel)}</text>"));

        for (int index = 0; index < withPoints.Length; index++)
        {
            ChartSeries item = withPoints[index];
            var path = new StringBuilder();
            foreach ((double x, double y) in item.points)
                path.Append(Format($"{(path.Length == 0 ? "M" : "L")}{ScaleX(x):0.##} {ScaleY(y):0.##} "));
            string dash = item.emphasis ? "none" : Dashes[index % Dashes.Length];
            string classes = "series s" + (index % 5) + (item.emphasis ? " emphasis" : "");
            svg.Append(Format(
                $"<path class=\"{classes}\" d=\"{path}\" fill=\"none\" stroke-dasharray=\"{dash}\"><title>{Escape(item.name)}</title></path>"));
        }

        svg.Append("</svg>");

        var legend = new StringBuilder("<ul class=\"legend\">");
        for (int index = 0; index < withPoints.Length; index++)
            legend.Append(
                $"<li><span class=\"swatch s{index % 5}\" aria-hidden=\"true\"></span>{Escape(withPoints[index].name)}</li>");
        legend.Append("</ul>");

        return svg + legend.ToString();
    }

    /// <summary>Escapes text taken from logs and configs before it reaches the page.</summary>
    public static string Escape(string? text) => (text ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
