using DevCommander.Agents;
using DevCommander.Data;
using DevCommander.Git;
using DevCommander.Integrations.Telegram;
using DevCommander.Missions;
using DevCommander.Options;
using DevCommander.Orchestration;
using DevCommander.Process;
using DevCommander.Runtimes;
using DevCommander.Sandbox;
using DevCommander.Services;
using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NovaCore.Agents;
using NovaCore.Agents.Persistence.EntityFramework;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<DevCommanderOptions>()
    .Bind(builder.Configuration.GetSection(DevCommanderOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<DevCommanderOptions>, DevCommanderOptionsValidator>();
builder.Services.AddOptions<TelegramOptions>()
    .Bind(builder.Configuration.GetSection(TelegramOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<TelegramOptions>, TelegramOptionsValidator>();

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IRuntimePaths, RuntimePaths>();
builder.Services.AddSingleton<IRuntimeGitPaths, RuntimeGitPaths>();
builder.Services.AddDbContextFactory<AppDbContext>((sp, options) =>
{
    var paths = sp.GetRequiredService<IRuntimePaths>();
    paths.EnsureInitialized();
    options.UseSqlite($"Data Source={paths.DatabasePath};Default Timeout=30;Pooling=True");
});
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
builder.Services.AddDefaultEfCorePersistence<AppDbContext>();
builder.Services.AddSingleton<IPricingSource>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<DevCommanderOptions>>().Value;
    var host = PricingSources.FromHost((providerId, modelId) =>
    {
        foreach (var agent in new[] { opts.Agents.Commander, opts.Agents.Planner, opts.Agents.Critic })
        {
            if (string.Equals(agent.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(agent.Model, modelId, StringComparison.OrdinalIgnoreCase)
                && agent.InputPerMTokens is { } input
                && agent.OutputPerMTokens is { } output)
            {
                return new ModelPricing { InputPerMTokens = input, OutputPerMTokens = output };
            }
        }

        return null;
    });
    var pricing = PricingSources.Composite(host, PricingSources.BuiltIn);
    foreach (var agent in new[] { opts.Agents.Commander, opts.Agents.Planner, opts.Agents.Critic })
    {
        if (pricing.Get(agent.ProviderId, agent.Model) is null)
        {
            throw new InvalidOperationException(
                $"Configured model '{agent.ProviderId}::{agent.Model}' has no pricing.");
        }
    }

    return pricing;
});
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IWorkerSandbox, BubblewrapWorkerSandbox>();
builder.Services.AddSingleton<IGitWorkspaceService, GitWorkspaceService>();
builder.Services.AddSingleton<IRuntimeAdapter, ClaudeRuntimeAdapter>();
builder.Services.AddSingleton<IRuntimeAdapter, CodexRuntimeAdapter>();
builder.Services.AddSingleton<IRuntimeAdapter, CursorRuntimeAdapter>();
builder.Services.AddSingleton<IRuntimeAdapter, OpenCodeRuntimeAdapter>();
builder.Services.AddSingleton<RuntimeRegistry>();
builder.Services.AddSingleton<IRuntimeRegistry>(sp => sp.GetRequiredService<RuntimeRegistry>());
builder.Services.AddSingleton<IRepositoryService, RepositoryService>();
builder.Services.AddSingleton<IMissionStartService, MissionStartService>();
builder.Services.AddSingleton<IMissionPlanner, MissionPlanner>();
builder.Services.AddSingleton<ICriticService, CriticService>();
builder.Services.AddSingleton<IVerifierService, VerifierService>();
builder.Services.AddSingleton<IApprovalService, ApprovalService>();
builder.Services.AddSingleton<IStateTransitionService, StateTransitionService>();
builder.Services.AddSingleton<ICostAccountingService, CostAccountingService>();
builder.Services.AddSingleton<INotificationOutbox, NotificationOutbox>();
builder.Services.AddSingleton<ISquadLoop, SquadLoop>();
builder.Services.AddSingleton<IMissionRuntimeRegistry, MissionRuntimeRegistry>();
builder.Services.AddSingleton<IMissionCoordinator, MissionCoordinator>();
builder.Services.AddSingleton<IMissionCommands, MissionCommands>();
builder.Services.AddSingleton<ITelegramMessenger, TelegramMessenger>();
builder.Services.AddSingleton<CommanderDispatcher>();
builder.Services.AddDevCommanderAgents(builder.Configuration);
builder.Services.AddHostedService<DatabaseInitializerHostedService>();
builder.Services.AddHostedService<RuntimeCapabilityProbeHostedService>();
builder.Services.AddHostedService<StartupReconciliationService>();
builder.Services.AddHostedService<TelegramPollingService>();
builder.Services.AddHostedService<TelegramInboxProcessorService>();
builder.Services.AddHostedService<NotificationFlusherService>();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();

public partial class Program;
