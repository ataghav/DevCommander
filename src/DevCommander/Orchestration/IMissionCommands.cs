namespace DevCommander.Orchestration;

/// <summary>
/// Deterministic command facade used by external transports.
/// </summary>
public interface IMissionCommands
{
    Task<string> ListMissionsAsync(long chatId, CancellationToken ct);
    Task<string> StartAsync(string missionSlug, long chatId, CancellationToken ct);
    Task<string> StatusAsync(string missionSlug, long chatId, CancellationToken ct);
    Task<string> ApproveAsync(Guid approvalId, long chatId, CancellationToken ct);
    Task<string> StopAsync(string missionSlug, string repoId, long chatId, CancellationToken ct);
    Task<string> ContinueAsync(string missionSlug, string repoId, string? guidance, long chatId, CancellationToken ct);
    Task<string> AgentCostsAsync(CancellationToken ct);
}
