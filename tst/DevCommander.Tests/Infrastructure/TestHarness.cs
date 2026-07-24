using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Integrations.Telegram;
using DevCommander.Missions;
using DevCommander.Options;
using DevCommander.Process;
using DevCommander.Runtimes;
using DevCommander.Sandbox;
using DevCommander.Services;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace DevCommander.Tests.Infrastructure;

public sealed class TestDataRoot : IDisposable
{
    public TestDataRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DevCommander.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        foreach (var name in new[] { "missions", "repos", "worktrees", "runtime-state" })
            Directory.CreateDirectory(System.IO.Path.Combine(Path, name));
    }

    public string Path { get; }
    public string Missions => System.IO.Path.Combine(Path, "missions");
    public string Repos => System.IO.Path.Combine(Path, "repos");
    public string Worktrees => System.IO.Path.Combine(Path, "worktrees");
    public string RuntimeState => System.IO.Path.Combine(Path, "runtime-state");

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class TestHostFactory : IDisposable
{
    private readonly ServiceProvider _provider;

    public TestHostFactory(
        Action<DevCommanderOptions>? configure = null,
        IMissionPlanner? planner = null,
        IEnumerable<IRuntimeAdapter>? adapters = null)
    {
        Data = new TestDataRoot();
        Options = new DevCommanderOptions { DataRoot = Data.Path, DefaultBudgetUsd = 5m };
        configure?.Invoke(Options);
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<DevCommanderOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<IOptions<TelegramOptions>>(Microsoft.Extensions.Options.Options.Create(new TelegramOptions { AllowedChatIds = [42] }));
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IRuntimePaths, RuntimePaths>();
        services.AddDbContextFactory<AppDbContext>(o =>
            o.UseSqlite($"Data Source={System.IO.Path.Combine(Data.Path, "devcommander.db")};Default Timeout=30;Pooling=True"));
        services.AddSingleton<IProcessRunner, ScriptedProcessRunner>();
        services.AddSingleton<IWorkerSandbox, FakeWorkerSandbox>();
        services.AddSingleton<ITelegramMessenger, RecordingTelegramMessenger>();
        services.AddSingleton<IMissionPlanner>(planner ?? new ScriptedMissionPlanner());
        foreach (var adapter in adapters ?? RuntimeKinds.All.Select(kind => new ScriptedRuntimeAdapter(kind)))
            services.AddSingleton(typeof(IRuntimeAdapter), adapter);
        services.AddSingleton<IRuntimeRegistry, RuntimeRegistry>();
        services.AddSingleton<IRepositoryService, RepositoryService>();
        services.AddSingleton<ICostAccountingService, CostAccountingService>();
        services.AddSingleton<INotificationOutbox, NotificationOutbox>();
        _provider = services.BuildServiceProvider(validateScopes: true);
        _provider.GetRequiredService<IRuntimePaths>().EnsureInitialized();
        using var db = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public TestDataRoot Data { get; }
    public DevCommanderOptions Options { get; }
    public IServiceProvider Services => _provider;
    public IDbContextFactory<AppDbContext> DbFactory => _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    public ScriptedProcessRunner ProcessRunner => (ScriptedProcessRunner)_provider.GetRequiredService<IProcessRunner>();
    public FakeWorkerSandbox Sandbox => (FakeWorkerSandbox)_provider.GetRequiredService<IWorkerSandbox>();
    public RecordingTelegramMessenger Messenger => (RecordingTelegramMessenger)_provider.GetRequiredService<ITelegramMessenger>();

    public void Dispose()
    {
        _provider.Dispose();
        Data.Dispose();
    }
}

public static class RuntimeKinds
{
    public static readonly RuntimeKind[] All = [RuntimeKind.Claude, RuntimeKind.Codex, RuntimeKind.Cursor, RuntimeKind.OpenCode];
}

public sealed class ScriptedMissionPlanner : IMissionPlanner
{
    public MissionPlan Plan { get; set; } = new([]);
    public int Calls { get; private set; }

    public Task<MissionPlan> PlanAsync(MissionSpecDocument spec, Guid? missionId, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Plan);
    }
}

public sealed class ScriptedRuntimeAdapter(RuntimeKind kind) : IRuntimeAdapter
{
    public RuntimeKind Kind { get; } = kind;
    public Queue<RuntimeResult> Results { get; } = new();
    public int Starts { get; private set; }
    public int Resumes { get; private set; }

    public Task<RuntimeResult> StartAsync(RuntimeRunRequest request, Func<ProcessStarted, CancellationToken, Task> onStarted, CancellationToken ct)
    {
        Starts++;
        return CompleteAsync(onStarted, ct);
    }

    public Task<RuntimeResult> ResumeAsync(string sessionId, RuntimeRunRequest request, Func<ProcessStarted, CancellationToken, Task> onStarted, CancellationToken ct)
    {
        Resumes++;
        return CompleteAsync(onStarted, ct);
    }

    private async Task<RuntimeResult> CompleteAsync(Func<ProcessStarted, CancellationToken, Task> onStarted, CancellationToken ct)
    {
        await onStarted(new ProcessStarted(123, DateTimeOffset.UtcNow), ct);
        return Results.Count > 0
            ? Results.Dequeue()
            : new RuntimeResult("session", "done", 0, null, null, FailureKind.None);
    }
}

public sealed class ScriptedProcessRunner : IProcessRunner
{
    public Queue<ProcessCompletion> Completions { get; } = new();
    public List<ProcessStartRequest> Requests { get; } = [];

    public Task<IProcessExecution> StartAsync(ProcessStartRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var completion = Completions.Count > 0
            ? Completions.Dequeue()
            : new ProcessCompletion(0, "", "", false);
        return Task.FromResult<IProcessExecution>(new CompletedProcess(completion));
    }

    private sealed class CompletedProcess(ProcessCompletion completion) : IProcessExecution
    {
        public int Pid => 123;
        public DateTimeOffset StartedAt => DateTimeOffset.UtcNow;
        public Task<ProcessCompletion> Completion { get; } = Task.FromResult(completion);
        public Task KillTreeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

public sealed class RecordingTelegramMessenger : ITelegramMessenger
{
    private int _nextMessageId;
    public List<(long ChatId, string Text)> Sends { get; } = [];
    public List<(long ChatId, int MessageId, string Text)> Cards { get; } = [];
    public List<(long ChatId, int MessageId, string Text)> Edits { get; } = [];
    public List<IReadOnlyList<(string Command, string Description)>> CommandMenus { get; } = [];
    public void Configure() { }
    public void Configure(ITelegramBotClient botClient) { }
    public Task SendTextAsync(long chatId, string text, CancellationToken ct, ParseMode? parseMode = null)
    {
        Sends.Add((chatId, text));
        return Task.CompletedTask;
    }

    public Task<int?> SendCardAsync(long chatId, string text, CancellationToken ct, ParseMode? parseMode = null)
    {
        var id = ++_nextMessageId;
        Cards.Add((chatId, id, text));
        return Task.FromResult<int?>(id);
    }

    public Task EditMessageTextAsync(long chatId, int messageId, string text, CancellationToken ct, ParseMode? parseMode = null)
    {
        Edits.Add((chatId, messageId, text));
        return Task.CompletedTask;
    }

    public Task SetMyCommandsAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken ct)
    {
        CommandMenus.Add(commands);
        return Task.CompletedTask;
    }
}

public static class TestMissions
{
    public static string Spec(params string[] repoIds) =>
        $$"""
        ## Repositories
        {{string.Join(Environment.NewLine, repoIds.Select(x => "- " + x))}}
        ## Goal
        Deliver it.
        ## In-scope
        Everything listed.
        ## Out-of-scope
        Nothing else.
        ## Verification commands
        {{string.Join(Environment.NewLine, repoIds.Select(x => "### " + x + Environment.NewLine + "repo default"))}}
        ## Acceptance criteria
        It works.
        ## Runtime preference
        default: claude
        """;

    public static Repo Repo(string id, RuntimeKind runtime = RuntimeKind.Claude) =>
        new() { Id = id, Source = id, DefaultBranch = "main", DefaultRuntime = runtime, VerifyCommandsJson = "[\"echo ok\"]", GatedOpsJson = "[]" };
}
