using DevCommander.Data;
using DevCommander.Git;
using DevCommander.Process;
using DevCommander.Services;
using DevCommander.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace DevCommander.Tests;

public sealed class DiTests
{
    [Fact]
    public void ScopedServicesAndKeyedFactories_ResolveInValidScopes()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedProbe>();
        services.AddKeyedSingleton("planner", new KeyedProbe("planner"));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ScopedProbe>());
        Assert.Equal("planner", scope.ServiceProvider.GetRequiredKeyedService<KeyedProbe>("planner").Name);
    }

    private sealed class ScopedProbe;
    private sealed record KeyedProbe(string Name);
}

public sealed class MigrationTests
{
    [Fact]
    public async Task DatabaseMigrate_CreatesApplicationAndNovaCoreTables()
    {
        using var root = new TestDataRoot();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root.Path, "migrated.db")}")
            .Options;
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync();
        await using var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        Assert.Contains("Missions", tables);
        Assert.Contains("agent_sessions", tables);
        Assert.Contains("agent_messages", tables);
        Assert.Contains("agent_memories", tables);
    }
}

public sealed class GitTests
{
    [Fact]
    public async Task PushToMainOrMaster_IsRejectedBeforeAnyGitPush()
    {
        using var host = new TestHostFactory();
        var git = new GitWorkspaceService(new TestGitPaths(host.Data.Repos), host.ProcessRunner,
            NullLogger<GitWorkspaceService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            git.PushMissionBranchAsync("repo", host.Data.Path, "main", default));

        Assert.Contains("main/master", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.ProcessRunner.Requests);
    }

    [Fact]
    public async Task ExplicitMissionRef_PushesToLocalBareRemote()
    {
        using var root = new TestDataRoot();
        var remote = Path.Combine(root.Path, "remote.git");
        var seed = Path.Combine(root.Path, "seed");
        await GitAsync(root.Path, "init", "--bare", remote);
        await GitAsync(root.Path, "init", "-b", "main", seed);
        await GitAsync(seed, "config", "user.email", "test@example.invalid");
        await GitAsync(seed, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
        await GitAsync(seed, "add", "README.md");
        await GitAsync(seed, "commit", "-m", "seed");
        await GitAsync(seed, "remote", "add", "origin", remote);
        await GitAsync(seed, "push", "origin", "main");

        var runner = new ProcessRunner(TimeProvider.System);
        var git = new GitWorkspaceService(new TestGitPaths(Path.Combine(root.Path, "clones")), runner,
            NullLogger<GitWorkspaceService>.Instance);
        await git.EnsureCloneAsync("repo", remote, "main", default);
        var worktree = await git.EnsureWorktreeAsync("repo", Guid.NewGuid(), "release",
            Path.Combine(root.Path, "worktree"), "main", default);
        await File.WriteAllTextAsync(Path.Combine(worktree.WorktreePath, "change.txt"), "change");
        await git.CommitAllAsync(worktree.WorktreePath, "change", default);

        await git.PushMissionBranchAsync("repo", worktree.WorktreePath, "release", default);

        var references = await GitAsync(remote, "show-ref", "--verify", "refs/heads/mission/repo/release");
        Assert.NotEmpty(references);
    }

    private sealed class TestGitPaths(string root) : IRuntimeGitPaths
    {
        public string GetRepoClonePath(string repoId) => Path.Combine(root, repoId);
    }

    private static async Task<string> GitAsync(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout;
    }
}

public sealed class ContentionTests
{
    [Fact]
    public async Task ConcurrentOutboxWriters_PersistEveryDistinctLogicalKey()
    {
        using var host = new TestHostFactory();
        var outbox = host.Services.GetRequiredService<INotificationOutbox>();
        var writes = Enumerable.Range(0, 4).Select(async i =>
        {
            await using var db = await host.DbFactory.CreateDbContextAsync();
            await outbox.EnqueueInTransactionAsync(db, 42, $"contention:{i}", DevCommander.Domain.NotificationSeverity.Info, "event", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        });
        await Task.WhenAll(writes);

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(4, await verify.Notifications.CountAsync());
    }
}
