using DevCommander.Integrations.Telegram;
using DevCommander.Services;

namespace DevCommander.Tests;

public sealed class CostReportTelegramFormatterTests
{
    [Fact]
    public void FormatHtml_UsesSectionsAndTotalsBlock()
    {
        var report = new CostLedgerReport(
            [
                new AgentCostSummary("commander", 2, 0.002512m, 0.002512m, 140, 107, IsEstimated: false),
                new AgentCostSummary("coder:Claude", 1, 0.5m, 0.5m, 0, 0, IsEstimated: true),
            ],
            HostLlmExactUsd: 0.002512m,
            CodingBestEffortUsd: 0.5m,
            GrandTotalUsd: 0.502512m);

        var html = CostReportTelegramFormatter.FormatHtml(report);

        Assert.Contains("<b>LLM costs</b>", html);
        Assert.Contains("<b>Host agents</b>", html);
        Assert.Contains("<b>commander</b>", html);
        Assert.Contains("<code>$0.002512</code>", html);
        Assert.Contains("<b>Claude</b>", html);
        Assert.Contains("<pre>", html);
        Assert.Contains("Total", html);
        Assert.Contains("$0.502512", html);
    }
}
