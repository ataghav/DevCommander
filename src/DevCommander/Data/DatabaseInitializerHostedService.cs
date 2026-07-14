using DevCommander.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DevCommander.Options;

namespace DevCommander.Data;

public sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "devcommander-design");
        Directory.CreateDirectory(dataRoot);
        var dbPath = Path.Combine(dataRoot, "devcommander.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath};Default Timeout=30;Pooling=True")
            .Options;
        return new AppDbContext(options);
    }
}

public sealed class DatabaseInitializerHostedService(
    IServiceProvider serviceProvider,
    IRuntimePaths paths,
    ILogger<DatabaseInitializerHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        paths.EnsureInitialized();

        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(cancellationToken);

        await using var conn = db.Database.GetDbConnection();
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        var mode = (await cmd.ExecuteScalarAsync(cancellationToken))?.ToString();
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected SQLite journal_mode=WAL, got '{mode}'.");
        }

        logger.LogInformation("DevCommander data root: {DataRoot}", paths.DataRoot);
        logger.LogInformation("SQLite journal_mode={JournalMode}; database={DatabasePath}", mode, paths.DatabasePath);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class SqliteBusyRetry
{
    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> action, int maxAttempts = 8, CancellationToken ct = default)
    {
        var delay = TimeSpan.FromMilliseconds(25);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsBusy(ex) && attempt < maxAttempts)
            {
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 1000));
            }
        }
    }

    public static Task ExecuteAsync(Func<Task> action, int maxAttempts = 8, CancellationToken ct = default) =>
        ExecuteAsync(async () =>
        {
            await action();
            return 0;
        }, maxAttempts, ct);

    private static bool IsBusy(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException!)
        {
            if (cur.Message.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase)
                || cur.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
