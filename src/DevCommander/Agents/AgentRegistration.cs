using DevCommander.Agents.Tools;
using DevCommander.Options;
using NovaCore.Agents;
using NovaCore.Agents.Providers.OpenAI;

namespace DevCommander.Agents;

public static class AgentRegistration
{
    public static IServiceCollection AddDevCommanderAgents(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DevCommanderOptions.SectionName).Get<DevCommanderOptions>() ?? new();
        services.AddModelProfiles(p => p
            .Add("commander", _ => Build(options.Agents.Commander))
            .Add("planner", _ => Build(options.Agents.Planner))
            .Add("critic", _ => Build(options.Agents.Critic)));

        services.AddAgentFactory("commander", f => f.Model("commander").Store().Defaults(new AgentSpec
        {
            Instructions = CommanderInstructions.Text,
            Loop = new LoopPolicy { Mode = LoopMode.Chat, MaxToolRounds = 25 },
            Summarize = new SummarizationConfig { EveryNTurns = 10, KeepRecentTurns = 10 }
        }));
        services.AddAgentFactory("planner", f => f.Model("planner").Defaults(new AgentSpec
        {
            Instructions = PlannerInstructions.Text,
            Loop = LoopPolicy.SingleResponse
        }));
        services.AddAgentFactory("critic", f => f.Model("critic").Defaults(new AgentSpec
        {
            Instructions = CriticInstructions.Text,
            Loop = LoopPolicy.SingleResponse
        }));
        services.AddSingleton<ICommanderCapability, RegisterRepositoryTool>();
        services.AddSingleton<ICommanderCapability, ListRepositoriesTool>();
        return services;
    }

    private static ILlmProvider Build(OpenAiCompatibleProviderOptions options)
    {
        var key = Environment.GetEnvironmentVariable(options.ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Environment variable '{options.ApiKeyEnvVar}' is required.");
        return OpenAiCompatible.Create(
            baseUrl: options.BaseUrl, apiKey: key, model: options.Model,
            providerId: options.ProviderId, timeout: TimeSpan.FromMinutes(options.TimeoutMinutes));
    }
}
