using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevCommander.Process;
using Microsoft.Extensions.Logging;

namespace DevCommander.Git;

public interface IGitWorkspaceService
{
    Task EnsureCloneAsync(string repoId, string source, string defaultBranch, CancellationToken ct);
    Task<WorktreeInfo> EnsureWorktreeAsync(
        string repoId,
        Guid missionId,
        string branch,
        string worktreePath,
        string defaultBranch,
        CancellationToken ct);
    Task<string> GetHeadShaAsync(string worktreePath, CancellationToken ct);
    Task<string> GetDiffAsync(string worktreePath, string baselineCommit, CancellationToken ct);
    Task<bool> HasChangesAsync(string worktreePath, string baselineCommit, CancellationToken ct);
    Task<string> CommitAllAsync(string worktreePath, string message, CancellationToken ct);
    Task PushBranchAsync(string repoId, string worktreePath, string branch, CancellationToken ct);
    Task RemoveWorktreeAsync(string repoId, string worktreePath, CancellationToken ct);
}

public sealed record WorktreeInfo(string WorktreePath, string Branch, string BaseCommit);

public sealed class GitWorkspaceService(
    IRuntimeGitPaths paths,
    IProcessRunner processRunner,
    ILogger<GitWorkspaceService> logger) : IGitWorkspaceService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task EnsureCloneAsync(string repoId, string source, string defaultBranch, CancellationToken ct)
    {
        await withRepoLock(repoId, async () =>
        {
            var repoPath = paths.GetRepoClonePath(repoId);
            if (Directory.Exists(Path.Combine(repoPath, ".git")))
            {
                await RunGitAsync(repoPath, ["fetch", "origin", defaultBranch], ct);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(repoPath)!);
            if (Directory.Exists(source) || source.StartsWith('/') || Regex.IsMatch(source, @"^[A-Za-z]:\\"))
            {
                // Local path: clone from filesystem
                await RunGitAsync(Path.GetDirectoryName(repoPath)!, ["clone", "--branch", defaultBranch, source, repoPath], ct);
            }
            else
            {
                await RunGitAsync(Path.GetDirectoryName(repoPath)!, ["clone", "--branch", defaultBranch, source, repoPath], ct);
            }
        }, ct);
    }

    public async Task<WorktreeInfo> EnsureWorktreeAsync(
        string repoId,
        Guid missionId,
        string branch,
        string worktreePath,
        string defaultBranch,
        CancellationToken ct)
    {
        return await withRepoLock(repoId, async () =>
        {
            var repoPath = paths.GetRepoClonePath(repoId);

            if (Directory.Exists(worktreePath))
            {
                var adopt = await TryAdoptAsync(worktreePath, branch, missionId, repoId, ct);
                if (adopt is not null)
                {
                    return adopt;
                }

                throw new InvalidOperationException(
                    $"Existing worktree at '{worktreePath}' does not match mission/gitdir/branch; refusing to adopt.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
            await RunGitAsync(repoPath, ["fetch", "origin", defaultBranch], ct);
            var baseCommit = (await RunGitAsync(repoPath, ["rev-parse", $"origin/{defaultBranch}"], ct)).StdOut.Trim();

            // Create branch from base if needed, then worktree.
            var branchExists = await RunGitAsync(repoPath, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], ct, allowFail: true);
            if (branchExists.ExitCode != 0)
            {
                await RunGitAsync(repoPath, ["branch", branch, baseCommit], ct);
            }

            await RunGitAsync(repoPath, ["worktree", "add", worktreePath, branch], ct);
            return new WorktreeInfo(worktreePath, branch, baseCommit);
        }, ct);
    }

    public async Task<string> GetHeadShaAsync(string worktreePath, CancellationToken ct)
    {
        var result = await RunGitAsync(worktreePath, ["rev-parse", "HEAD"], ct);
        return result.StdOut.Trim();
    }

    public async Task<string> GetDiffAsync(string worktreePath, string baselineCommit, CancellationToken ct)
    {
        var committed = await RunGitAsync(worktreePath, ["diff", $"{baselineCommit}..HEAD"], ct);
        var unstaged = await RunGitAsync(worktreePath, ["diff"], ct);
        var staged = await RunGitAsync(worktreePath, ["diff", "--cached"], ct);
        var untracked = await RunGitAsync(worktreePath, ["ls-files", "--others", "--exclude-standard"], ct);

        var sb = new StringBuilder();
        sb.Append(committed.StdOut);
        if (!string.IsNullOrWhiteSpace(staged.StdOut))
        {
            sb.AppendLine();
            sb.Append(staged.StdOut);
        }

        if (!string.IsNullOrWhiteSpace(unstaged.StdOut))
        {
            sb.AppendLine();
            sb.Append(unstaged.StdOut);
        }

        if (!string.IsNullOrWhiteSpace(untracked.StdOut))
        {
            sb.AppendLine();
            sb.AppendLine("# untracked:");
            sb.Append(untracked.StdOut);
        }

        return sb.ToString();
    }

    public async Task<bool> HasChangesAsync(string worktreePath, string baselineCommit, CancellationToken ct)
    {
        var diff = await GetDiffAsync(worktreePath, baselineCommit, ct);
        return !string.IsNullOrWhiteSpace(diff);
    }

    public async Task<string> CommitAllAsync(string worktreePath, string message, CancellationToken ct)
    {
        await RunGitAsync(worktreePath, ["add", "-A"], ct);
        var status = await RunGitAsync(worktreePath, ["status", "--porcelain"], ct);
        if (string.IsNullOrWhiteSpace(status.StdOut))
        {
            return await GetHeadShaAsync(worktreePath, ct);
        }

        await RunGitAsync(worktreePath, ["-c", "user.email=devcommander@local", "-c", "user.name=DevCommander", "commit", "-m", message], ct);
        return await GetHeadShaAsync(worktreePath, ct);
    }

    public async Task PushBranchAsync(string repoId, string worktreePath, string branch, CancellationToken ct)
    {
        await withRepoLock(repoId, async () =>
        {
            if (branch.EndsWith("/main", StringComparison.OrdinalIgnoreCase)
                || branch.EndsWith("/master", StringComparison.OrdinalIgnoreCase)
                || branch.Equals("main", StringComparison.OrdinalIgnoreCase)
                || branch.Equals("master", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Push to main/master is rejected.");
            }

            var refspec = $"HEAD:refs/heads/{branch}";
            // Explicit non-force push only.
            await RunGitAsync(worktreePath, ["push", "origin", refspec], ct);
        }, ct);
    }

    public async Task RemoveWorktreeAsync(string repoId, string worktreePath, CancellationToken ct)
    {
        await withRepoLock(repoId, async () =>
        {
            var repoPath = paths.GetRepoClonePath(repoId);
            if (Directory.Exists(worktreePath))
            {
                await RunGitAsync(repoPath, ["worktree", "remove", "--force", worktreePath], ct, allowFail: true);
                if (Directory.Exists(worktreePath))
                {
                    Directory.Delete(worktreePath, recursive: true);
                }
            }
        }, ct);
    }

    private async Task<WorktreeInfo?> TryAdoptAsync(
        string worktreePath, string branch, Guid missionId, string repoId, CancellationToken ct)
    {
        try
        {
            var headBranch = (await RunGitAsync(worktreePath, ["rev-parse", "--abbrev-ref", "HEAD"], ct)).StdOut.Trim();
            if (!string.Equals(headBranch, branch, StringComparison.Ordinal))
            {
                return null;
            }

            var gitDir = (await RunGitAsync(worktreePath, ["rev-parse", "--git-dir"], ct)).StdOut.Trim();
            var common = (await RunGitAsync(worktreePath, ["rev-parse", "--git-common-dir"], ct)).StdOut.Trim();
            var expectedClone = Path.GetFullPath(paths.GetRepoClonePath(repoId));
            var commonFull = Path.GetFullPath(Path.IsPathRooted(common) ? common : Path.Combine(worktreePath, common));
            if (!commonFull.StartsWith(expectedClone, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(commonFull, Path.Combine(expectedClone, ".git"), StringComparison.OrdinalIgnoreCase))
            {
                // Still accept if git-common-dir resolves under the clone
                if (!commonFull.Contains(expectedClone, StringComparison.OrdinalIgnoreCase)
                    && !gitDir.Contains(repoId, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            var baseCommit = (await RunGitAsync(worktreePath, ["merge-base", "HEAD", "HEAD"], ct)).StdOut.Trim();
            var head = await GetHeadShaAsync(worktreePath, ct);
            _ = missionId;
            return new WorktreeInfo(worktreePath, branch, head);
        }
        catch
        {
            return null;
        }
    }

    private async Task<T> withRepoLock<T>(string repoId, Func<Task<T>> action, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(repoId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task withRepoLock(string repoId, Func<Task> action, CancellationToken ct) =>
        await withRepoLock(repoId, async () =>
        {
            await action();
            return 0;
        }, ct);

    private async Task<ProcessCompletion> RunGitAsync(
        string cwd,
        IReadOnlyList<string> args,
        CancellationToken ct,
        bool allowFail = false)
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_ASKPASS"] = "echo",
            ["LANG"] = "C.UTF-8",
        };

        var exec = await processRunner.StartAsync(new ProcessStartRequest(
            FileName: "git",
            Arguments: args,
            WorkingDirectory: cwd,
            Environment: env), ct);

        var completion = await exec.Completion.WaitAsync(ct);
        if (!allowFail && completion.ExitCode != 0)
        {
            logger.LogWarning("git {Args} failed exit={Exit}", string.Join(' ', args), completion.ExitCode);
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {completion.ExitCode}): {Trim(completion.StdErr)}");
        }

        return completion;
    }

    private static string Trim(string s) =>
        s.Length <= 500 ? s : s[..500] + "…";
}

public interface IRuntimeGitPaths
{
    string GetRepoClonePath(string repoId);
}

public sealed class RuntimeGitPaths(DevCommander.Workspace.IRuntimePaths paths) : IRuntimeGitPaths
{
    public string GetRepoClonePath(string repoId) => Path.Combine(paths.ReposDir, repoId);
}

public static class FailureSignature
{
    public static string Compute(IEnumerable<string> blockingFindings, string? command, int? exitCode, IEnumerable<string> failureLines)
    {
        var parts = blockingFindings
            .Concat(failureLines)
            .Select(Normalize)
            .Where(s => s.Length > 0)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(command))
        {
            parts.Insert(0, "cmd:" + Normalize(command));
        }

        if (exitCode is not null)
        {
            parts.Insert(0, "exit:" + exitCode.Value);
        }

        var material = string.Join('\n', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..32];
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return "";
        }

        var collapsed = Regex.Replace(s.Trim(), @"\s+", " ");
        return collapsed.ToLowerInvariant();
    }
}
