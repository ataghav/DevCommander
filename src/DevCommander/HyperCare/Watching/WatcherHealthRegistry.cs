using System.Collections.Concurrent;

namespace DevCommander.HyperCare.Watching;

public sealed record WatcherHealth(DateTimeOffset? LastSuccessAt, string? LastError);

public interface IWatcherHealthRegistry
{
    void MarkSuccess(string serviceId, DateTimeOffset at);
    void MarkError(string serviceId, string error);
    void Reset();
    IReadOnlyDictionary<string, WatcherHealth> Snapshot();
}

/// <summary>
/// In-memory per-service telemetry (logging/debug only). /hc_status reads the durable
/// HyperCareSourceHealth rows instead, so restarts never blank it (FR-HC-032).
/// </summary>
public sealed class WatcherHealthRegistry : IWatcherHealthRegistry
{
    private readonly ConcurrentDictionary<string, WatcherHealth> _health = new(StringComparer.OrdinalIgnoreCase);

    public void MarkSuccess(string serviceId, DateTimeOffset at) =>
        _health.AddOrUpdate(serviceId,
            _ => new WatcherHealth(at, null),
            (_, _) => new WatcherHealth(at, null));

    public void MarkError(string serviceId, string error) =>
        _health.AddOrUpdate(serviceId,
            _ => new WatcherHealth(null, error),
            (_, prior) => prior with { LastError = error });

    public void Reset() => _health.Clear();

    public IReadOnlyDictionary<string, WatcherHealth> Snapshot() =>
        new Dictionary<string, WatcherHealth>(_health, StringComparer.OrdinalIgnoreCase);
}
