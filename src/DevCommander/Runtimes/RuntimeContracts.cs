using System.Text.Json;
using DevCommander.Domain;
using DevCommander.Process;
using DevCommander.Sandbox;

namespace DevCommander.Runtimes;

public interface IRuntimeAdapter
{
    RuntimeKind Kind { get; }
    Task<RuntimeResult> StartAsync(
        RuntimeRunRequest request,
        Func<ProcessStarted, CancellationToken, Task> onStarted,
        CancellationToken ct);
    Task<RuntimeResult> ResumeAsync(
        string sessionId,
        RuntimeRunRequest request,
        Func<ProcessStarted, CancellationToken, Task> onStarted,
        CancellationToken ct);
}

public sealed record RuntimeRunRequest(
    string WorktreePath,
    string RuntimeHomePath,
    string Prompt,
    decimal? RemainingCostCapUsd = null,
    string? PromptFilePath = null);

public sealed record ProcessStarted(int Pid, DateTimeOffset StartedAt);

public sealed record RuntimeUsage(int? InputTokens, int? OutputTokens);

public sealed record RuntimeResult(
    string? SessionId,
    string FinalMessage,
    int ExitCode,
    decimal? CostUsd,
    RuntimeUsage? Usage,
    FailureKind FailureKind,
    bool CostIsEstimated = false);

public interface IRuntimeRegistry
{
    IRuntimeAdapter Get(RuntimeKind kind);
    bool IsAvailable(RuntimeKind kind);
    string? UnavailableReason(RuntimeKind kind);
    IReadOnlyDictionary<RuntimeKind, string?> Availability { get; }
}

public sealed class RuntimeRegistry : IRuntimeRegistry
{
    private readonly Dictionary<RuntimeKind, IRuntimeAdapter> _adapters;
    private readonly Dictionary<RuntimeKind, string?> _unavailable = new();

    public RuntimeRegistry(IEnumerable<IRuntimeAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(a => a.Kind);
        foreach (var kind in Enum.GetValues<RuntimeKind>())
        {
            if (!_adapters.ContainsKey(kind))
            {
                _unavailable[kind] = "adapter not registered";
            }
            else
            {
                _unavailable[kind] = null;
            }
        }
    }

    public IRuntimeAdapter Get(RuntimeKind kind) =>
        _adapters.TryGetValue(kind, out var a)
            ? a
            : throw new InvalidOperationException($"Runtime {kind} is not registered.");

    public bool IsAvailable(RuntimeKind kind) =>
        _adapters.ContainsKey(kind) && string.IsNullOrEmpty(_unavailable.GetValueOrDefault(kind));

    public string? UnavailableReason(RuntimeKind kind) => _unavailable.GetValueOrDefault(kind);

    public void MarkUnavailable(RuntimeKind kind, string reason) => _unavailable[kind] = reason;

    public void MarkAvailable(RuntimeKind kind) => _unavailable[kind] = null;

    public IReadOnlyDictionary<RuntimeKind, string?> Availability => _unavailable;
}

public abstract class RuntimeAdapterBase(
    IWorkerSandbox sandbox,
    TimeProvider _) : IRuntimeAdapter
{
    public abstract RuntimeKind Kind { get; }
    protected abstract string Executable { get; }
    protected abstract IReadOnlyList<string> BuildStartArgs(RuntimeRunRequest request);
    protected abstract IReadOnlyList<string> BuildResumeArgs(string sessionId, RuntimeRunRequest request);
    protected abstract RuntimeResult Parse(ProcessCompletion completion, bool cancelled);

    public Task<RuntimeResult> StartAsync(
        RuntimeRunRequest request,
        Func<ProcessStarted, CancellationToken, Task> onStarted,
        CancellationToken ct) =>
        RunAsync(BuildStartArgs(request), request, onStarted, ct);

    public Task<RuntimeResult> ResumeAsync(
        string sessionId,
        RuntimeRunRequest request,
        Func<ProcessStarted, CancellationToken, Task> onStarted,
        CancellationToken ct) =>
        RunAsync(BuildResumeArgs(sessionId, request), request, onStarted, ct);

    private async Task<RuntimeResult> RunAsync(
        IReadOnlyList<string> args,
        RuntimeRunRequest request,
        Func<ProcessStarted, CancellationToken, Task> onStarted,
        CancellationToken ct)
    {
        var env = BuildEnvironment(request);
        IProcessExecution execution;
        try
        {
            execution = await sandbox.StartWorkerAsync(new WorkerSandboxRequest(
                Executable: Executable,
                Arguments: args,
                WorktreePath: request.WorktreePath,
                RuntimeHomePath: request.RuntimeHomePath,
                ExtraEnvironment: env), ct);
        }
        catch (Exception ex) when (ex.Message.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeResult(null, ex.Message, -1, null, null, FailureKind.Other);
        }

        await onStarted(new ProcessStarted(execution.Pid, execution.StartedAt), ct);

        try
        {
            var completion = await execution.Completion.WaitAsync(ct);
            return Parse(completion, cancelled: false);
        }
        catch (OperationCanceledException)
        {
            await execution.KillTreeAsync(CancellationToken.None);
            var completion = await execution.Completion;
            return Parse(completion, cancelled: true) with { FailureKind = FailureKind.Cancelled };
        }
    }

    protected virtual Dictionary<string, string?> BuildEnvironment(RuntimeRunRequest request) => new();

    protected static JsonElement? TryParseJson(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        // Prefer last JSON object line for NDJSON streams.
        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{') && !line.StartsWith('['))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // try whole body next
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected static FailureKind ClassifyFailure(int exitCode, string stderr, bool cancelled)
    {
        if (cancelled)
        {
            return FailureKind.Cancelled;
        }

        var text = (stderr ?? "") + " ";
        if (text.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return FailureKind.Authentication;
        }

        if (text.Contains("session", StringComparison.OrdinalIgnoreCase)
            && (text.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || text.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || text.Contains("expired", StringComparison.OrdinalIgnoreCase)))
        {
            return FailureKind.SessionUnavailable;
        }

        if (text.Contains("network", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase))
        {
            return FailureKind.TransientNetwork;
        }

        if (text.Contains("usage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || exitCode == 2)
        {
            return FailureKind.InvalidInvocation;
        }

        return FailureKind.Other;
    }
}
