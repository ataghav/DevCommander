using System.Text;
using System.Text.Json;
using DevCommander.Domain;
using DevCommander.Domain.Entities;

namespace DevCommander.HyperCare;

/// <summary>
/// Telegram card and command-menu text. CTAs are slash commands in the message body (FR-HC-030(a),
/// ADR-HC-006): whole underscore tokens like /go_ab12cd34 are tappable bot_command entities, and the
/// argument-bearing typed forms stay visible for auditability.
/// </summary>
public static class HyperCareCards
{
    public static readonly IReadOnlyList<(string Command, string Description)> NormalCommands =
    [
        ("/missions", "List missions"),
        ("/start", "Start a mission: /start {slug}"),
        ("/status", "Mission status: /status {slug}"),
        ("/approve", "Approve a gated command: /approve {id}"),
        ("/stop", "Stop a squad: /stop {slug} {repo}"),
        ("/continue", "Continue a squad: /continue {slug} {repo} [guidance]"),
        ("/costs", "LLM cost ledger"),
        ("/hc_on", "Activate Hyper-Care mode"),
    ];

    public static readonly IReadOnlyList<(string Command, string Description)> HyperCareCommands =
    [
        ("/hc_status", "Hyper-Care session status"),
        ("/hc_off", "Deactivate Hyper-Care"),
        ("/go", "Accept issue: /go {id} [severity]"),
        ("/nogo", "Suppress issue: /nogo {id}"),
        ("/severity", "Set severity: /severity {id} {low|medium|high|critical}"),
        ("/priority", "Set priority: /priority {id} {n}"),
        ("/hold", "Prefer issue for its repo: /hold {id}"),
        ("/unhold", "Requeue a held issue: /unhold {id}"),
        ("/approve", "Approve a gated command: /approve {id}"),
        ("/stop", "Stop a fix squad: /stop hc-{id} {repo}"),
        ("/continue", "Continue a fix squad: /continue hc-{id} {repo} [guidance]"),
        ("/costs", "LLM cost ledger"),
    ];

    public static string FormatIssueCard(HyperCareIssue issue, HyperCareSeverity sessionDefault)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"🔎 HC issue {issue.ShortId} — {issue.ServiceId}   [{issue.Status}]");
        var severityNote = issue.Severity == sessionDefault ? " (default)" : "";
        sb.AppendLine($"Sev: {Fmt(issue.Severity)}{severityNote} · Prio: {issue.Priority} · "
            + $"Seen {issue.OccurrenceCount}× ({issue.FirstSeenAt:HH:mm}–{issue.LastSeenAt:HH:mm}Z)");
        sb.AppendLine(issue.Summary);
        if (FirstSample(issue.AttributesJson) is { Length: > 0 } sample)
        {
            sb.AppendLine($"Evidence: {Truncate(sample, 400)}");
        }

        sb.AppendLine("——");
        if (issue.Status == HyperCareIssueStatus.AwaitingDecision)
        {
            sb.AppendLine($"Accept: /go_{issue.ShortId}   Suppress: /nogo_{issue.ShortId}");
            sb.AppendLine($"Typed: /go {issue.ShortId} high · /severity {issue.ShortId} critical · /priority {issue.ShortId} 5");
        }
        else if (issue.Status is HyperCareIssueStatus.Queued or HyperCareIssueStatus.Held)
        {
            sb.AppendLine($"Prefer: /hold_{issue.ShortId}   Requeue: /unhold_{issue.ShortId}");
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatStatus(
        HyperCareSession session,
        IReadOnlyList<HyperCareIssue> issues,
        IReadOnlyDictionary<Guid, (string Slug, MissionStatus Status)> missions,
        IReadOnlyList<HyperCareSourceHealth> health)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"🩺 Hyper-Care {session.ShortId}: {session.Status}");
        sb.AppendLine($"Budget: ${session.AccountedCostUsd:0.00} / ${session.BudgetUsd:0.00} · "
            + $"Concurrency: {issues.Count(i => i.Status == HyperCareIssueStatus.Running)}/{session.MaxConcurrency}");

        if (issues.Count > 0)
        {
            var counts = issues.GroupBy(i => i.Status).OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Count()}");
            sb.AppendLine($"Issues: {string.Join(" · ", counts)}");
        }
        else
        {
            sb.AppendLine("Issues: none yet");
        }

        foreach (var running in issues.Where(i => i.Status == HyperCareIssueStatus.Running))
        {
            var mission = running.MissionId is { } id && missions.TryGetValue(id, out var m)
                ? $"{m.Slug}: {m.Status}"
                : "starting…";
            sb.AppendLine($"▶ {running.ShortId} ({running.RepoId}) → {mission}");
        }

        var queue = issues
            .Where(i => i.Status == HyperCareIssueStatus.Queued)
            .OrderByDescending(i => i.HoldPreferred)
            .ThenByDescending(i => i.Priority)
            .ThenByDescending(i => i.Severity)
            .ThenBy(i => i.FirstSeenAt)
            .Take(5)
            .Select(i => i.ShortId + (i.HoldPreferred ? "*" : ""))
            .ToList();
        if (queue.Count > 0)
        {
            sb.AppendLine($"Queue: {string.Join(" → ", queue)}");
        }

        foreach (var h in health.OrderBy(x => x.ServiceId))
        {
            sb.AppendLine(h.LastError is null
                ? $"● {h.ServiceId}: ok{(h.LastSuccessAt is { } okAt ? $" ({okAt:HH:mm}Z)" : "")}"
                : $"○ {h.ServiceId}: degraded — {Truncate(h.LastError, 120)}"
                    + (h.LastErrorAt is { } errAt ? $" (as of {errAt:HH:mm}Z)" : ""));
        }

        return sb.ToString().TrimEnd();
    }

    public static string? FirstSample(string attributesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(attributesJson);
            if (doc.RootElement.TryGetProperty("samples", out var arr)
                && arr.ValueKind == JsonValueKind.Array
                && arr.GetArrayLength() > 0)
            {
                return arr[arr.GetArrayLength() - 1].GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string Fmt(HyperCareSeverity severity) => severity.ToString().ToLowerInvariant();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
