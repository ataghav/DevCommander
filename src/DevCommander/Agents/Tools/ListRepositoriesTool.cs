using DevCommander.Services;
using NovaCore.Agents;

namespace DevCommander.Agents.Tools;

public sealed record ListRepositoriesArgs();

public sealed record ListedRepository(string RepoId, string Source, string DefaultBranch, string DefaultRuntime);

public sealed record ListRepositoriesResult(IReadOnlyList<ListedRepository> Repositories);

public sealed class ListRepositoriesTool(IServiceScopeFactory scopeFactory)
    : Capability<ListRepositoriesArgs, ListRepositoriesResult>, ICommanderCapability
{
    public override string Name => "ListRepositories";
    public override string Description => "List registered repositories (id, source, default branch, default runtime).";

    protected override async ValueTask<CapabilityResult<ListRepositoriesResult>> InvokeAsync(
        ListRepositoriesArgs args, CapabilityContext ctx, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repositories = scope.ServiceProvider.GetRequiredService<IRepositoryService>();
        var repos = await repositories.ListAsync(ct);
        var listed = repos
            .Select(r => new ListedRepository(r.Id, r.Source, r.DefaultBranch, r.DefaultRuntime.ToString()))
            .ToList();
        return CapabilityResult<ListRepositoriesResult>.Ok(new(listed));
    }
}
