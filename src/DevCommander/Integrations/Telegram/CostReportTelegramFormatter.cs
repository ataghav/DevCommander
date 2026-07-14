using System.Globalization;
using System.Net;
using System.Text;
using DevCommander.Services;

namespace DevCommander.Integrations.Telegram;

/// <summary>Formats cost ledger reports for Telegram HTML parse mode.</summary>
public static class CostReportTelegramFormatter
{
    public static string FormatHtml(CostLedgerReport report)
    {
        if (report.Lines.Count == 0)
        {
            return "<b>LLM costs</b>\n\n<i>No costs recorded yet.</i>";
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>LLM costs</b>");
        sb.AppendLine();

        var host = report.Lines
            .Where(l => !l.AgentRole.StartsWith("coder:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var coding = report.Lines
            .Where(l => l.AgentRole.StartsWith("coder:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        sb.AppendLine("<b>Host agents</b> <i>exact</i>");
        if (host.Count == 0)
        {
            sb.AppendLine("• <i>none yet</i>");
        }
        else
        {
            foreach (var line in host)
            {
                sb.Append("• <b>").Append(Esc(line.AgentRole)).Append("</b> — ")
                    .Append(line.Runs).Append(line.Runs == 1 ? " run" : " runs")
                    .Append(" · <code>").Append(Money(line.TotalCostUsd)).Append("</code>");
                sb.AppendLine();
                sb.Append("  <i>")
                    .Append(line.InputTokens.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" in · ")
                    .Append(line.OutputTokens.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" out</i>");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.AppendLine("<b>Coding agents</b> <i>best-effort when unmetered</i>");
        if (coding.Count == 0)
        {
            sb.AppendLine("• <i>none yet</i>");
        }
        else
        {
            foreach (var line in coding)
            {
                var name = line.AgentRole.StartsWith("coder:", StringComparison.OrdinalIgnoreCase)
                    ? line.AgentRole["coder:".Length..]
                    : line.AgentRole;
                var tag = line.IsEstimated ? "best-effort" : "exact";
                sb.Append("• <b>").Append(Esc(name)).Append("</b> — ")
                    .Append(line.Runs).Append(line.Runs == 1 ? " run" : " runs")
                    .Append(" · <code>").Append(Money(line.TotalCostUsd)).Append("</code>")
                    .Append(" <i>").Append(tag).Append("</i>");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
        sb.Append("<pre>")
            .Append("Host LLM  ").Append(Money(report.HostLlmExactUsd).PadLeft(12)).Append('\n')
            .Append("Coding    ").Append(Money(report.CodingBestEffortUsd).PadLeft(12)).Append('\n')
            .Append("Total     ").Append(Money(report.GrandTotalUsd).PadLeft(12))
            .Append("</pre>");

        return sb.ToString().TrimEnd();
    }

    private static string Money(decimal value) =>
        "$" + value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Esc(string value) => WebUtility.HtmlEncode(value);
}
