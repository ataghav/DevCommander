using System.Text.Json;
using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Git;
using DevCommander.HyperCare;
using DevCommander.HyperCare.Watching;
using DevCommander.Integrations.Telegram;
using DevCommander.Missions;
using DevCommander.Options;
using DevCommander.Orchestration;
using DevCommander.Process;
using DevCommander.Runtimes;
using DevCommander.Services;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DevCommander.Tests.Infrastructure;

public sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FakeGrafanaClient : IGrafanaClient
{
    public string? HealthProblem { get; set; }
    public Queue<string> Responses { get; } = new();
    public string DefaultResponse { get; set; } = "{}";
    public Exception? QueryFailure { get; set; }
    public List<(GrafanaQueryConfig Query, DateTimeOffset From, DateTimeOffset To)> Queries { get; } = [];

    public Task<string?> CheckHealthAsync(string baseUrl, string token, CancellationToken ct) =>
        Task.FromResult(HealthProblem);

    public Task<string> QueryAsync(
        string baseUrl, string token, GrafanaQueryConfig query,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        Queries.Add((query, from, to));
        return QueryFailure is { } failure
            ? Task.FromException<string>(failure)
            : Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : DefaultResponse);
    }
}

public sealed class FakeAzureCliRunner : IAzureCliRunner
{
    public string? IdentityProblem { get; set; }
    public Queue<(bool Ok, string Evidence)> CheckResults { get; } = new();

    public Task<string?> CheckIdentityAsync(CancellationToken ct) => Task.FromResult(IdentityProblem);

    public Task<(bool Ok, string Evidence)> RunCheckAsync(AzureCheckConfig check, CancellationToken ct) =>
        Task.FromResult(CheckResults.Count > 0 ? CheckResults.Dequeue() : (true, ""));
}

public sealed class FakeGitHubCli : IGitHubCli
{
    public string? AuthProblem { get; set; }
    public string PrUrl { get; set; } = "https://github.com/acme/checkout/pull/7";
    public Exception? CreateFailure { get; set; }
    public List<(string RepoDir, string Head, string Base, string Title)> CreatedPrs { get; } = [];

    public Task<string?> CheckAuthAsync(CancellationToken ct) => Task.FromResult(AuthProblem);

    public Task<string> CreatePullRequestAsync(
        string repoDir, string headBranch, string baseBranch, string title, string body, CancellationToken ct)
    {
        if (CreateFailure is { } failure)
        {
            throw failure;
        }

        CreatedPrs.Add((repoDir, headBranch, baseBranch, title));
        return Task.FromResult(PrUrl);
    }
}

public sealed class ScriptedTriageService : ITriageService
{
    public Queue<TriageResult> Results { get; } = new();
    public List<string> Contexts { get; } = [];
    public int Calls { get; private set; }

    public Task<TriageOutcome> TriageAsync(string serviceId, string boundedContext, CancellationToken ct)
    {
        Calls++;
        Contexts.Add(boundedContext);
        if (Results.Count == 0)
        {
            throw new InvalidOperationException("No scripted triage result.");
        }

        return Task.FromResult(new TriageOutcome(Results.Dequeue(), 0.01m));
    }
}

public sealed class ScriptedInvestigateService : IInvestigateService
{
    public InvestigateResult Result { get; set; } =
        new("Null payment handle", ["checkout"], "Fix the null payment handle in PaymentService.Capture and add a regression test.", "n/a");
    public int Calls { get; private set; }

    public Task<InvestigateOutcome> InvestigateAsync(HyperCareIssue issue, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new InvestigateOutcome(Result, 0.02m));
    }
}

public sealed class RecordingGit : IGitWorkspaceService
{
    public List<(string RepoId, string Branch)> Pushes { get; } = [];
    public string Diff { get; set; } = "diff --git a/x b/x";

    public Task EnsureCloneAsync(string repoId, string source, string defaultBranch, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<WorktreeInfo> EnsureWorktreeAsync(
        string repoId, Guid missionId, string branch, string worktreePath, string defaultBranch, CancellationToken ct)
    {
        Directory.CreateDirectory(worktreePath);
        return Task.FromResult(new WorktreeInfo(worktreePath, branch, "base"));
    }

    public Task<string> GetHeadShaAsync(string worktreePath, CancellationToken ct) => Task.FromResult("abc123");
    public Task<string> GetDiffAsync(string worktreePath, string baselineCommit, CancellationToken ct) => Task.FromResult(Diff);
    public Task<bool> HasChangesAsync(string worktreePath, string baselineCommit, CancellationToken ct) => Task.FromResult(true);
    public Task<string> CommitAllAsync(string worktreePath, string message, CancellationToken ct) => Task.FromResult("abc123");

    public Task PushBranchAsync(string repoId, string worktreePath, string branch, CancellationToken ct)
    {
        Pushes.Add((repoId, branch));
        return Task.CompletedTask;
    }

    public Task RemoveWorktreeAsync(string repoId, string worktreePath, CancellationToken ct) => Task.CompletedTask;
}

public sealed class ApprovingCritic : ICriticService
{
    public int Calls { get; private set; }

    public Task<CriticVerdict> ReviewAsync(string taskDescription, string diff, Guid? missionId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new CriticVerdict(true, [], null));
    }
}

/// <summary>
/// Full-stack Hyper-Care host: real orchestration (SquadLoop/MissionCoordinator/HyperCareCoordinator)
/// over scripted externals (git, coder runtimes, Grafana, az, gh, triage, investigate, Telegram).
/// </summary>
public sealed class HyperCareTestHost : IDisposable
{
    public const string GrafanaTokenEnvVar = "HC_TEST_GRAFANA_TOKEN";

    private readonly ServiceProvider _provider;

    public HyperCareTestHost(Action<DevCommanderOptions>? configure = null)
    {
        Environment.SetEnvironmentVariable(GrafanaTokenEnvVar, "test-token");
        Data = new TestDataRoot();
        Options = new DevCommanderOptions { DataRoot = Data.Path, DefaultBudgetUsd = 5m };
        configure?.Invoke(Options);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Options.IOptions<DevCommanderOptions>>(
            Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<Microsoft.Extensions.Options.IOptions<TelegramOptions>>(
            Microsoft.Extensions.Options.Options.Create(new TelegramOptions { AllowedChatIds = [42] }));
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Time);
        services.AddSingleton<IRuntimePaths, RuntimePaths>();
        services.AddDbContextFactory<AppDbContext>(o =>
            o.UseSqlite($"Data Source={System.IO.Path.Combine(Data.Path, "devcommander.db")};Default Timeout=30;Pooling=True"));
        services.AddSingleton<IProcessRunner, ScriptedProcessRunner>();
        services.AddSingleton<ITelegramMessenger, RecordingTelegramMessenger>();
        services.AddSingleton<IMissionPlanner, ScriptedMissionPlanner>();
        foreach (var kind in RuntimeKinds.All)
        {
            services.AddSingleton(typeof(IRuntimeAdapter), new ScriptedRuntimeAdapter(kind));
        }

        services.AddSingleton<IRuntimeRegistry, RuntimeRegistry>();
        services.AddSingleton<IRepositoryService, RepositoryService>();
        services.AddSingleton<ICostAccountingService, CostAccountingService>();
        services.AddSingleton<INotificationOutbox, NotificationOutbox>();
        services.AddSingleton<IStateTransitionService, StateTransitionService>();
        services.AddSingleton<IAgentCostTracker, AgentCostTracker>();
        services.AddSingleton<IGitWorkspaceService, RecordingGit>();
        services.AddSingleton<ICriticService, ApprovingCritic>();
        services.AddSingleton<IVerifierService, VerifierService>();
        services.AddSingleton<IApprovalService, ApprovalService>();
        services.AddSingleton<IMissionStartService, MissionStartService>();
        services.AddSingleton<ISquadLoop, SquadLoop>();
        services.AddSingleton<IMissionRuntimeRegistry, MissionRuntimeRegistry>();
        services.AddSingleton<IMissionCoordinator, MissionCoordinator>();
        services.AddSingleton<IMissionCommands, MissionCommands>();

        services.AddSingleton<IGrafanaClient, FakeGrafanaClient>();
        services.AddSingleton<IAzureCliRunner, FakeAzureCliRunner>();
        services.AddSingleton<IGitHubCli, FakeGitHubCli>();
        services.AddSingleton<IWatcherHealthRegistry, WatcherHealthRegistry>();
        services.AddSingleton<ServiceWatcherDeps>();
        services.AddSingleton<IHyperCareSessionGate, HyperCareSessionGate>();
        services.AddSingleton<IHyperCareEventLog, HyperCareEventLog>();
        services.AddSingleton<IHyperCareBudget, HyperCareBudget>();
        services.AddSingleton<IHyperCareIssueService, HyperCareIssueService>();
        services.AddSingleton<IHyperCareActivationValidator, HyperCareActivationValidator>();
        services.AddSingleton<ITriageService, ScriptedTriageService>();
        services.AddSingleton<IInvestigateService, ScriptedInvestigateService>();
        services.AddSingleton<IHyperCareFixTrackService, HyperCareFixTrackService>();
        services.AddSingleton<IHyperCareCommands, HyperCareCommands>();
        services.AddSingleton<HyperCareCoordinator>();

        _provider = services.BuildServiceProvider(validateScopes: true);
        _provider.GetRequiredService<IRuntimePaths>().EnsureInitialized();
        using var db = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public TestDataRoot Data { get; }
    public DevCommanderOptions Options { get; }
    public MutableTimeProvider Time { get; } = new();
    public IServiceProvider Services => _provider;
    public IDbContextFactory<AppDbContext> DbFactory => _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    public RecordingTelegramMessenger Messenger => (RecordingTelegramMessenger)_provider.GetRequiredService<ITelegramMessenger>();
    public FakeGrafanaClient Grafana => (FakeGrafanaClient)_provider.GetRequiredService<IGrafanaClient>();
    public FakeAzureCliRunner Azure => (FakeAzureCliRunner)_provider.GetRequiredService<IAzureCliRunner>();
    public FakeGitHubCli GitHub => (FakeGitHubCli)_provider.GetRequiredService<IGitHubCli>();
    public ScriptedTriageService Triage => (ScriptedTriageService)_provider.GetRequiredService<ITriageService>();
    public ScriptedInvestigateService Investigate => (ScriptedInvestigateService)_provider.GetRequiredService<IInvestigateService>();
    public ScriptedMissionPlanner Planner => (ScriptedMissionPlanner)_provider.GetRequiredService<IMissionPlanner>();
    public RecordingGit Git => (RecordingGit)_provider.GetRequiredService<IGitWorkspaceService>();
    public IHyperCareCommands Commands => _provider.GetRequiredService<IHyperCareCommands>();
    public IMissionCommands MissionCommands => _provider.GetRequiredService<IMissionCommands>();
    public IHyperCareIssueService Issues => _provider.GetRequiredService<IHyperCareIssueService>();
    public HyperCareCoordinator Coordinator => _provider.GetRequiredService<HyperCareCoordinator>();
    public ServiceWatcherDeps WatcherDeps => _provider.GetRequiredService<ServiceWatcherDeps>();

    public async Task<Repo> RegisterRepoAsync(string id)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        var repo = TestMissions.Repo(id);
        db.Repos.Add(repo);
        await db.SaveChangesAsync();
        return repo;
    }

    public void WriteConfig(object config)
    {
        var dir = System.IO.Path.Combine(Data.Path, "hypercare");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            System.IO.Path.Combine(dir, "config.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    public object DefaultConfig(
        int maxConcurrency = 2,
        decimal budgetUsd = 25m,
        params (string ServiceId, string RepoId)[] servicesOverride)
    {
        (string ServiceId, string RepoId)[] serviceList = servicesOverride.Length > 0
            ? servicesOverride
            : [("checkout-api", "checkout")];
        return new
        {
            maxConcurrency,
            budgetUsd,
            fixTrackBudgetUsd = 2.0m,
            triageEstimateUsd = 0.05m,
            investigateEstimateUsd = 0.10m,
            defaultSeverity = "medium",
            defaultPriority = 0,
            pollIntervalSeconds = 60,
            grafana = new { baseUrl = "https://grafana.test/", tokenEnvVar = GrafanaTokenEnvVar },
            services = serviceList.Select(s => new
            {
                id = s.ServiceId,
                repoId = s.RepoId,
                enabled = true,
                grafanaQueries = new[] { new { name = "errors", method = "POST", path = "api/ds/query", bodyTemplate = "{\"from\":\"{fromMs}\",\"to\":\"{toMs}\"}" } },
                include = new[] { "(?i)error|exception" },
                exclude = new[] { "(?i)healthcheck" },
            }).ToArray(),
        };
    }

    public async Task<HyperCareSession> ActivateAsync(long chatId = 42)
    {
        var reply = await Commands.ActivateAsync(chatId, default);
        Assert.Contains("Hyper-Care active", reply);
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.HyperCareSessions.AsNoTracking().SingleAsync(s => s.Status == HyperCareSessionStatus.Running);
    }

    public ServiceWatcher CreateWatcher(HyperCareSession session, string serviceId = "checkout-api")
    {
        var config = HyperCareConfigLoader.Parse(session.ConfigSnapshot, "test").Config!;
        var service = config.Services.Single(s => string.Equals(s.Id, serviceId, StringComparison.OrdinalIgnoreCase));
        return new ServiceWatcher(WatcherDeps, session.Id, config, service);
    }

    public async Task<HyperCareIssue> GetIssueAsync(Guid issueId)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.HyperCareIssues.AsNoTracking().SingleAsync(i => i.Id == issueId);
    }

    public async Task<IReadOnlyList<HyperCareIssue>> GetIssuesAsync(Guid sessionId)
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        return await db.HyperCareIssues.AsNoTracking().Where(i => i.SessionId == sessionId).ToListAsync();
    }

    /// <summary>Repeatedly ticks the coordinator until the condition holds (background fix-track work is async).</summary>
    public async Task TickUntilAsync(Func<Task<bool>> condition, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Coordinator.TickAsync(default);
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition not reached before timeout.");
    }

    public void Dispose()
    {
        Coordinator.Shutdown();
        _provider.Dispose();
        Data.Dispose();
    }
}
