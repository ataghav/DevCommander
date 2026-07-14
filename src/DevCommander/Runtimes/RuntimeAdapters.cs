using System.Text.Json;
using DevCommander.Domain;
using DevCommander.Options;
using DevCommander.Process;
using DevCommander.Sandbox;
using Microsoft.Extensions.Options;

namespace DevCommander.Runtimes;

public sealed class ClaudeRuntimeAdapter(
    IWorkerSandbox sandbox,
    TimeProvider time,
    IOptions<DevCommanderOptions> options) : RuntimeAdapterBase(sandbox, time)
{
    private readonly ClaudeRuntimeOptions _opts = options.Value.Runtimes.Claude;
    public override RuntimeKind Kind => RuntimeKind.Claude;
    protected override string Executable => _opts.Executable;

    protected override IReadOnlyList<string> BuildStartArgs(RuntimeRunRequest request)
    {
        var args = new List<string> { "-p", "--output-format", "json" };
        if (request.RemainingCostCapUsd is { } cap)
        {
            args.Add("--max-budget-usd");
            args.Add(cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add(request.Prompt);
        return args;
    }

    protected override IReadOnlyList<string> BuildResumeArgs(string sessionId, RuntimeRunRequest request)
    {
        var args = new List<string> { "-p", "--output-format", "json", "--resume", sessionId };
        if (request.RemainingCostCapUsd is { } cap)
        {
            args.Add("--max-budget-usd");
            args.Add(cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add(request.Prompt);
        return args;
    }

    protected override Dictionary<string, string?> BuildEnvironment(RuntimeRunRequest request) =>
        new()
        {
            // Claude settings enable sandboxing with failIfUnavailable=true (defense in depth).
            ["CLAUDE_CODE_SANDBOX"] = "1",
            ["CLAUDE_CODE_SANDBOX_FAIL_IF_UNAVAILABLE"] = "true",
        };

    protected override RuntimeResult Parse(ProcessCompletion completion, bool cancelled)
    {
        if (cancelled)
        {
            return new RuntimeResult(null, completion.StdOut, completion.ExitCode, null, null, FailureKind.Cancelled);
        }

        var json = TryParseJson(completion.StdOut) ?? TryParseJson(completion.StdErr);
        if (json is null)
        {
            var failure = completion.ExitCode == 0 ? FailureKind.None : ClassifyFailure(completion.ExitCode, completion.StdErr, false);
            return new RuntimeResult(null, completion.StdOut, completion.ExitCode, null, null, failure);
        }

        var root = json.Value;
        var result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : completion.StdOut;
        var sessionId = root.TryGetProperty("session_id", out var s) ? s.GetString() : null;
        decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var d) ? d : null;
        var failureKind = completion.ExitCode == 0 ? FailureKind.None : ClassifyFailure(completion.ExitCode, completion.StdErr, false);
        return new RuntimeResult(sessionId, result, completion.ExitCode, cost, null, failureKind, CostIsEstimated: false);
    }
}

public sealed class CodexRuntimeAdapter(
    IWorkerSandbox sandbox,
    TimeProvider time,
    IOptions<DevCommanderOptions> options) : RuntimeAdapterBase(sandbox, time)
{
    private readonly CodexRuntimeOptions _opts = options.Value.Runtimes.Codex;
    public override RuntimeKind Kind => RuntimeKind.Codex;
    protected override string Executable => _opts.Executable;

    protected override IReadOnlyList<string> BuildStartArgs(RuntimeRunRequest request) =>
        ["exec", "--json", "--sandbox", "workspace-write", request.Prompt];

    protected override IReadOnlyList<string> BuildResumeArgs(string sessionId, RuntimeRunRequest request) =>
        ["exec", "resume", sessionId, "--json", "--sandbox", "workspace-write", request.Prompt];

    protected override RuntimeResult Parse(ProcessCompletion completion, bool cancelled)
    {
        if (cancelled)
        {
            return new RuntimeResult(null, completion.StdOut, completion.ExitCode, null, null, FailureKind.Cancelled);
        }

        string? sessionId = null;
        string? message = null;
        int? inputTokens = null;
        int? outputTokens = null;
        decimal? estimatedCost = null;

        foreach (var line in completion.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "thread.started" && root.TryGetProperty("thread_id", out var tid))
                {
                    sessionId = tid.GetString();
                }
                else if (root.TryGetProperty("thread", out var thread)
                         && thread.TryGetProperty("started", out var started)
                         && started.TryGetProperty("thread_id", out var tid2))
                {
                    sessionId = tid2.GetString();
                }

                if (type == "agent_message" && root.TryGetProperty("message", out var directMessage))
                {
                    message = directMessage.GetString();
                }
                else if (root.TryGetProperty("agent_message", out var am))
                {
                    message = am.ValueKind == JsonValueKind.String ? am.GetString()
                        : am.TryGetProperty("text", out var txt) ? txt.GetString() : message;
                }

                if (type == "turn.completed" && root.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var i))
                    {
                        inputTokens = i;
                    }

                    if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var o))
                    {
                        outputTokens = o;
                    }
                }
            }
            catch (JsonException)
            {
                // ignore unknown lines
            }
        }

        // Usage-derived cost is estimated unless provider billing is authoritative.
        if (inputTokens is not null || outputTokens is not null)
        {
            estimatedCost = ((inputTokens ?? 0) + (outputTokens ?? 0)) / 1_000_000m * 1.0m;
        }

        var failure = completion.ExitCode == 0 ? FailureKind.None : ClassifyFailure(completion.ExitCode, completion.StdErr, false);
        return new RuntimeResult(
            sessionId,
            message ?? completion.StdOut,
            completion.ExitCode,
            estimatedCost,
            new RuntimeUsage(inputTokens, outputTokens),
            failure,
            CostIsEstimated: estimatedCost is not null);
    }
}

public sealed class CursorRuntimeAdapter(
    IWorkerSandbox sandbox,
    TimeProvider time,
    IOptions<DevCommanderOptions> options) : RuntimeAdapterBase(sandbox, time)
{
    private readonly CursorRuntimeOptions _opts = options.Value.Runtimes.Cursor;
    public override RuntimeKind Kind => RuntimeKind.Cursor;
    protected override string Executable => _opts.Executable;

    protected override IReadOnlyList<string> BuildStartArgs(RuntimeRunRequest request) =>
    [
        "-p", "--output-format", "json", "--sandbox", "enabled", "--force", "--trust",
        "--workspace", request.WorktreePath, request.Prompt
    ];

    protected override IReadOnlyList<string> BuildResumeArgs(string sessionId, RuntimeRunRequest request) =>
    [
        "-p", "--output-format", "json", "--sandbox", "enabled", "--force", "--trust",
        "--workspace", request.WorktreePath, "--resume", sessionId, request.Prompt
    ];

    protected override RuntimeResult Parse(ProcessCompletion completion, bool cancelled)
    {
        if (cancelled)
        {
            return new RuntimeResult(null, completion.StdOut, completion.ExitCode, null, null, FailureKind.Cancelled);
        }

        var json = TryParseJson(completion.StdOut);
        string? sessionId = null;
        string result = completion.StdOut;
        if (json is { } root)
        {
            if (root.TryGetProperty("result", out var r))
            {
                result = r.GetString() ?? result;
            }

            if (root.TryGetProperty("session_id", out var s))
            {
                sessionId = s.GetString();
            }
        }

        var failure = completion.ExitCode == 0 ? FailureKind.None : ClassifyFailure(completion.ExitCode, completion.StdErr, false);
        // No usage/cost in output — host uses configured estimated reservation.
        return new RuntimeResult(sessionId, result, completion.ExitCode, null, null, failure, CostIsEstimated: true);
    }
}

public sealed class OpenCodeRuntimeAdapter(
    IWorkerSandbox sandbox,
    TimeProvider time,
    IOptions<DevCommanderOptions> options) : RuntimeAdapterBase(sandbox, time)
{
    private readonly OpenCodeRuntimeOptions _opts = options.Value.Runtimes.OpenCode;
    public override RuntimeKind Kind => RuntimeKind.OpenCode;
    protected override string Executable => _opts.Executable;

    protected override IReadOnlyList<string> BuildStartArgs(RuntimeRunRequest request) =>
        ["run", "--format", "json", "--auto", request.Prompt];

    protected override IReadOnlyList<string> BuildResumeArgs(string sessionId, RuntimeRunRequest request) =>
        ["run", "--format", "json", "--auto", "--session", sessionId, request.Prompt];

    protected override Dictionary<string, string?> BuildEnvironment(RuntimeRunRequest request) =>
        new()
        {
            // Host-owned permission config denying external_directory, push/deploy.
            ["OPENCODE_PERMISSION"] = """{"external_directory":"deny","push":"deny","deploy":"deny"}""",
        };

    protected override RuntimeResult Parse(ProcessCompletion completion, bool cancelled)
    {
        if (cancelled)
        {
            return new RuntimeResult(null, completion.StdOut, completion.ExitCode, null, null, FailureKind.Cancelled);
        }

        string? sessionId = null;
        var texts = new List<string>();
        decimal cost = 0m;
        var sawCost = false;

        foreach (var line in completion.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("sessionID", out var sid))
                {
                    sessionId = sid.GetString();
                }

                if (root.TryGetProperty("text", out var text))
                {
                    texts.Add(text.GetString() ?? "");
                }

                if (root.TryGetProperty("type", out var type) && type.GetString() == "step_finish"
                    && root.TryGetProperty("part", out var part)
                    && part.TryGetProperty("cost", out var c)
                    && c.TryGetDecimal(out var d))
                {
                    cost += d;
                    sawCost = true;
                }
            }
            catch (JsonException)
            {
                // ignore
            }
        }

        var failure = completion.ExitCode == 0 ? FailureKind.None : ClassifyFailure(completion.ExitCode, completion.StdErr, false);
        return new RuntimeResult(
            sessionId,
            texts.Count > 0 ? string.Join("\n", texts) : completion.StdOut,
            completion.ExitCode,
            sawCost ? cost : null,
            null,
            failure,
            CostIsEstimated: false);
    }
}
