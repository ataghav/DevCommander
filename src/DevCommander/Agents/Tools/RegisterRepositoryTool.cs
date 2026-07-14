using DevCommander.Domain;
using DevCommander.Services;
using NovaCore.Agents;

namespace DevCommander.Agents.Tools;

public sealed record RegisterRepositoryArgs(
    string RepoId, string Source, string DefaultBranch, RuntimeKind DefaultRuntime,
    string[] VerifyCommands, string[] GatedOps);

public sealed record RegisterRepositoryResult(string RepoId, string Source, RuntimeKind DefaultRuntime);

public sealed class RegisterRepositoryTool(
    IServiceScopeFactory scopeFactory)
    : Capability<RegisterRepositoryArgs, RegisterRepositoryResult>, ICommanderCapability
{
    public override string Name => "RegisterRepository";
    public override string Description => "Register or update a repository and its verification policy.";

    protected override async ValueTask<CapabilityResult<RegisterRepositoryResult>> InvokeAsync(
        RegisterRepositoryArgs args, CapabilityContext ctx, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repositories = scope.ServiceProvider.GetRequiredService<IRepositoryService>();
            var repo = await repositories.RegisterAsync(new(
                args.RepoId, args.Source, args.DefaultBranch, args.DefaultRuntime,
                args.VerifyCommands ?? [], args.GatedOps ?? []), ct);
            return CapabilityResult<RegisterRepositoryResult>.Ok(new(repo.Id, repo.Source, repo.DefaultRuntime));
        }
        catch (ArgumentException ex)
        {
            return CapabilityResult<RegisterRepositoryResult>.Error(ex.Message);
        }
    }
}
