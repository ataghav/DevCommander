using System.Text.Json;
using DevCommander.Process;

namespace DevCommander.HyperCare.Watching;

public interface IGitHubCli
{
    /// <summary>Returns null when `gh` auth is usable, or a problem description.</summary>
    Task<string?> CheckAuthAsync(CancellationToken ct);

    /// <summary>Creates (or finds) the PR for a pushed branch and returns its URL. Throws with details on failure.</summary>
    Task<string> CreatePullRequestAsync(
        string repoDir,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken ct);
}

public sealed class GitHubCli(IProcessRunner processRunner, ILogger<GitHubCli> logger) : IGitHubCli
{
    public async Task<string?> CheckAuthAsync(CancellationToken ct)
    {
        try
        {
            var completion = await RunAsync(Environment.CurrentDirectory, ["auth", "status"], ct);
            return completion.ExitCode == 0
                ? null
                : $"gh auth unusable (gh auth status exit {completion.ExitCode}): {Trim(completion.StdErr)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"gh unavailable: {ex.Message}";
        }
    }

    public async Task<string> CreatePullRequestAsync(
        string repoDir,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken ct)
    {
        var create = await RunAsync(repoDir,
            ["pr", "create", "--head", headBranch, "--base", baseBranch, "--title", title, "--body", body], ct);
        if (create.ExitCode == 0)
        {
            var url = ExtractUrl(create.StdOut) ?? ExtractUrl(create.StdErr);
            if (url is not null)
            {
                return url;
            }
        }

        // "already exists" (idempotent retry after crash) — resolve the existing PR's URL.
        if (create.StdErr.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || create.StdOut.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            var view = await RunAsync(repoDir, ["pr", "view", headBranch, "--json", "url"], ct);
            if (view.ExitCode == 0)
            {
                using var doc = JsonDocument.Parse(view.StdOut);
                if (doc.RootElement.TryGetProperty("url", out var urlProp) && urlProp.GetString() is { Length: > 0 } url)
                {
                    return url;
                }
            }
        }

        logger.LogWarning("gh pr create failed for {Branch}: exit={Exit} err={Err}",
            headBranch, create.ExitCode, Trim(create.StdErr));
        throw new InvalidOperationException(
            $"gh pr create failed for branch '{headBranch}' (exit {create.ExitCode}): {Trim(create.StdErr)}");
    }

    private async Task<ProcessCompletion> RunAsync(string cwd, IReadOnlyList<string> args, CancellationToken ct)
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["HOME"] = Environment.GetEnvironmentVariable("HOME"),
            ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE"),
            ["GH_TOKEN"] = Environment.GetEnvironmentVariable("GH_TOKEN"),
            ["GH_CONFIG_DIR"] = Environment.GetEnvironmentVariable("GH_CONFIG_DIR"),
            ["GIT_TERMINAL_PROMPT"] = "0",
        };

        var exec = await processRunner.StartAsync(new ProcessStartRequest(
            FileName: "gh",
            Arguments: args,
            WorkingDirectory: cwd,
            Environment: env), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            return await exec.Completion.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await exec.KillTreeAsync(CancellationToken.None);
            throw new TimeoutException($"gh {string.Join(' ', args)} timed out.");
        }
    }

    private static string? ExtractUrl(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
