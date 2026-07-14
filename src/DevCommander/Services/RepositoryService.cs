using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.Services;

public sealed record RegisterRepositoryRequest(
    string RepoId,
    string Source,
    string DefaultBranch,
    RuntimeKind DefaultRuntime,
    IReadOnlyList<string> VerifyCommands,
    IReadOnlyList<string> GatedOps);

public interface IRepositoryService
{
    Task<Repo> RegisterAsync(RegisterRepositoryRequest request, CancellationToken ct);
    Task<IReadOnlyList<Repo>> ListAsync(CancellationToken ct);
}

public sealed class RepositoryService(IDbContextFactory<AppDbContext> dbFactory) : IRepositoryService
{
    public async Task<Repo> RegisterAsync(RegisterRepositoryRequest request, CancellationToken ct)
    {
        var problems = Validate(request);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(Environment.NewLine, problems));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var repo = await db.Repos.SingleOrDefaultAsync(x => x.Id == request.RepoId.Trim(), ct);
        if (repo is null)
        {
            repo = new Repo { Id = request.RepoId.Trim() };
            db.Repos.Add(repo);
        }

        repo.Source = request.Source.Trim();
        repo.DefaultBranch = request.DefaultBranch.Trim();
        repo.DefaultRuntime = request.DefaultRuntime;
        repo.SetVerifyCommands(request.VerifyCommands.Select(x => x.Trim()));
        repo.SetGatedOps(request.GatedOps.Select(Normalize));
        await db.SaveChangesAsync(ct);
        return repo;
    }

    public async Task<IReadOnlyList<Repo>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Repos.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
    }

    private static List<string> Validate(RegisterRepositoryRequest request)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(request.RepoId) ||
            !request.RepoId.Trim().All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            problems.Add("repoId must contain only letters, numbers, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(request.Source)) problems.Add("source is required.");
        if (string.IsNullOrWhiteSpace(request.DefaultBranch)) problems.Add("defaultBranch is required.");
        if (request.VerifyCommands.Any(string.IsNullOrWhiteSpace)) problems.Add("verifyCommands cannot contain empty commands.");
        if (request.GatedOps.Any(string.IsNullOrWhiteSpace)) problems.Add("gatedOps cannot contain empty patterns.");
        return problems;
    }

    private static string Normalize(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");
}
