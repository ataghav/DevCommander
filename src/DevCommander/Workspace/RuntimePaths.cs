using DevCommander.Options;
using Microsoft.Extensions.Options;

namespace DevCommander.Workspace;

public interface IRuntimePaths
{
    string DataRoot { get; }
    string DatabasePath { get; }
    string MissionsDir { get; }
    string ReposDir { get; }
    string WorktreesDir { get; }
    string RuntimeStateDir { get; }
    string HyperCareDir { get; }
    string GetHyperCareConfigPath();
    string GetMissionSpecPath(string missionSlug);
    string GetWorktreePath(Guid missionId, string repoId);
    string GetSquadRuntimeHome(Guid missionId, string repoId);
    void EnsureInitialized();
}

public sealed class RuntimePaths(IOptions<DevCommanderOptions> options) : IRuntimePaths
{
    private readonly DevCommanderOptions _options = options.Value;

    public string DataRoot => Path.GetFullPath(_options.DataRoot);

    public string DatabasePath => Path.Combine(DataRoot, "devcommander.db");

    public string MissionsDir => Path.Combine(DataRoot, "missions");

    public string ReposDir => Path.Combine(DataRoot, "repos");

    public string WorktreesDir => Path.Combine(DataRoot, "worktrees");

    public string RuntimeStateDir => Path.Combine(DataRoot, "runtime-state");

    public string HyperCareDir => Path.Combine(DataRoot, "hypercare");

    /// <summary>Prefers hypercare/config.json; falls back to the SRS's extensionless hypercare/config.</summary>
    public string GetHyperCareConfigPath()
    {
        var jsonPath = Path.Combine(HyperCareDir, "config.json");
        var barePath = Path.Combine(HyperCareDir, "config");
        return File.Exists(jsonPath) || !File.Exists(barePath) ? jsonPath : barePath;
    }

    public string GetMissionSpecPath(string missionSlug) =>
        Path.Combine(MissionsDir, $"{missionSlug}.md");

    public string GetWorktreePath(Guid missionId, string repoId) =>
        Path.Combine(WorktreesDir, missionId.ToString("N"), repoId);

    public string GetSquadRuntimeHome(Guid missionId, string repoId) =>
        Path.Combine(RuntimeStateDir, missionId.ToString("N"), repoId, "home");

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(MissionsDir);
        Directory.CreateDirectory(ReposDir);
        Directory.CreateDirectory(WorktreesDir);
        Directory.CreateDirectory(RuntimeStateDir);
        Directory.CreateDirectory(HyperCareDir);
    }
}
