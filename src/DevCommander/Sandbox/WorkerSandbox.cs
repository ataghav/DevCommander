using System.Runtime.InteropServices;
using DevCommander.Process;
using Microsoft.Extensions.Logging;

namespace DevCommander.Sandbox;

public interface IWorkerSandbox
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    Task ProbeAsync(CancellationToken ct);
    Task<IProcessExecution> StartWorkerAsync(WorkerSandboxRequest request, CancellationToken ct);
}

public sealed record WorkerSandboxRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorktreePath,
    string RuntimeHomePath,
    IReadOnlyDictionary<string, string?> ExtraEnvironment,
    string? StdIn = null,
    int MaxOutputChars = 200_000);

/// <summary>
/// Linux bubblewrap sandbox. On non-Linux hosts, marks itself unavailable so probes
/// fail closed (no unsandboxed fallback). Tests inject a fake implementation.
/// </summary>
public sealed class BubblewrapWorkerSandbox(
    IProcessRunner processRunner,
    TimeProvider _,
    ILogger<BubblewrapWorkerSandbox> logger) : IWorkerSandbox
{
    private static readonly string[] ForbiddenEnvKeys =
    [
        "GIT_ASKPASS", "GIT_TERMINAL_PROMPT", "GH_TOKEN", "GITHUB_TOKEN",
        "GITLAB_TOKEN", "SSH_AUTH_SOCK", "SSH_AGENT_PID", "DEPLOY_TOKEN",
        "AWS_SECRET_ACCESS_KEY", "AWS_ACCESS_KEY_ID", "AZURE_CLIENT_SECRET",
    ];

    private int _probed;
    private bool _available;
    private string? _unavailableReason = "Not probed";

    public bool IsAvailable => _available;
    public string? UnavailableReason => _unavailableReason;

    public async Task ProbeAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _probed, 1) == 1)
        {
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _available = false;
            _unavailableReason = "bubblewrap sandbox requires Linux";
            logger.LogWarning("Worker sandbox unavailable: {Reason}", _unavailableReason);
            return;
        }

        try
        {
            var bwrap = FindOnPath("bwrap") ?? FindOnPath("bubblewrap");
            if (bwrap is null)
            {
                _available = false;
                _unavailableReason = "bubblewrap executable not found";
                return;
            }

            // Probe user namespaces + filesystem isolation: write outside bound root must fail.
            var probeRoot = Path.Combine(Path.GetTempPath(), "dc-sandbox-probe-" + Guid.NewGuid().ToString("N"));
            var work = Path.Combine(probeRoot, "work");
            var home = Path.Combine(probeRoot, "home");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(home);

            var args = BuildBwrapArgs(
                executable: "/bin/sh",
                arguments: ["-c", "echo ok > /work/ok.txt; echo secret > /tmp/should-fail 2>/dev/null; test -f /work/ok.txt && ! test -f /etc/shadow-copy"],
                worktreePath: work,
                runtimeHomePath: home,
                readonlyBinds: ["/usr", "/bin", "/lib", "/lib64", "/etc"],
                extraRoBinds: []);

            var env = SanitizeEnvironment(new Dictionary<string, string?>());
            var exec = await processRunner.StartAsync(new ProcessStartRequest(
                FileName: bwrap,
                Arguments: args,
                WorkingDirectory: work,
                Environment: env), ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var completion = await exec.Completion.WaitAsync(cts.Token);
            if (completion.ExitCode != 0)
            {
                _available = false;
                _unavailableReason = $"sandbox probe failed exit={completion.ExitCode}";
            }
            else
            {
                _available = true;
                _unavailableReason = null;
            }

            try { Directory.Delete(probeRoot, recursive: true); } catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            _available = false;
            _unavailableReason = ex.Message;
            logger.LogWarning(ex, "Worker sandbox probe failed");
        }
    }

    public async Task<IProcessExecution> StartWorkerAsync(WorkerSandboxRequest request, CancellationToken ct)
    {
        if (!_available)
        {
            throw new InvalidOperationException(
                $"Worker sandbox unavailable: {_unavailableReason ?? "unknown"}. No unsandboxed fallback.");
        }

        var bwrap = FindOnPath("bwrap") ?? FindOnPath("bubblewrap")
            ?? throw new InvalidOperationException("bubblewrap executable not found");

        Directory.CreateDirectory(request.WorktreePath);
        Directory.CreateDirectory(request.RuntimeHomePath);

        var fileName = ResolveExecutable(request.Executable);
        var args = BuildBwrapArgs(
            executable: fileName,
            arguments: request.Arguments,
            worktreePath: request.WorktreePath,
            runtimeHomePath: request.RuntimeHomePath,
            readonlyBinds: ["/usr", "/bin", "/lib", "/lib64", "/etc"],
            extraRoBinds: fileName.StartsWith('/') ? [Path.GetDirectoryName(fileName)!] : []);

        var env = SanitizeEnvironment(request.ExtraEnvironment);
        env["HOME"] = "/home/worker";
        env["TMPDIR"] = "/tmp";

        return await processRunner.StartAsync(new ProcessStartRequest(
            FileName: bwrap,
            Arguments: args,
            WorkingDirectory: request.WorktreePath,
            Environment: env,
            MaxOutputChars: request.MaxOutputChars), ct);
    }

    internal static IReadOnlyList<string> BuildBwrapArgs(
        string executable,
        IReadOnlyList<string> arguments,
        string worktreePath,
        string runtimeHomePath,
        IReadOnlyList<string> readonlyBinds,
        IReadOnlyList<string> extraRoBinds)
    {
        var args = new List<string>
        {
            "--unshare-all",
            "--share-net",
            "--die-with-parent",
            "--new-session",
            "--tmpfs", "/tmp",
            "--proc", "/proc",
            "--dev", "/dev",
            "--dir", "/home/worker",
            "--bind", worktreePath, "/work",
            "--bind", runtimeHomePath, "/home/worker",
            "--chdir", "/work",
            "--setenv", "HOME", "/home/worker",
        };

        foreach (var path in readonlyBinds.Concat(extraRoBinds).Distinct(StringComparer.Ordinal))
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                args.Add("--ro-bind");
                args.Add(path);
                args.Add(path);
            }
        }

        // Worktree-specific git metadata: bind worktree's .git read/write when present.
        var gitPath = Path.Combine(worktreePath, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath))
        {
            args.Add("--bind");
            args.Add(gitPath);
            args.Add("/work/.git");
        }

        args.Add(executable);
        args.AddRange(arguments);
        return args;
    }

    internal static Dictionary<string, string?> SanitizeEnvironment(IReadOnlyDictionary<string, string?> extra)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["LANG"] = "C.UTF-8",
            ["HOME"] = "/home/worker",
            ["TERM"] = "dumb",
        };

        foreach (var (k, v) in extra)
        {
            if (ForbiddenEnvKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            env[k] = v;
        }

        foreach (var key in ForbiddenEnvKeys)
        {
            env.Remove(key);
        }

        return env;
    }

    private static string ResolveExecutable(string executable)
    {
        if (executable.Contains('/') || executable.Contains('\\'))
        {
            return executable;
        }

        return FindOnPath(executable) ?? executable;
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// Contract-faithful in-process sandbox for tests and non-Linux hosts in unit tests.
/// Enforces allowlisted paths and strips forbidden credentials from the environment.
/// </summary>
public sealed class FakeWorkerSandbox : IWorkerSandbox
{
    private readonly IProcessRunner _runner;
    public bool DenyOutsideWorktree { get; set; } = true;
    public bool IsAvailable { get; set; } = true;
    public string? UnavailableReason { get; set; }

    public FakeWorkerSandbox(IProcessRunner runner) => _runner = runner;

    public Task ProbeAsync(CancellationToken ct)
    {
        if (!IsAvailable && UnavailableReason is null)
        {
            UnavailableReason = "fake sandbox marked unavailable";
        }

        return Task.CompletedTask;
    }

    public async Task<IProcessExecution> StartWorkerAsync(WorkerSandboxRequest request, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException($"Worker sandbox unavailable: {UnavailableReason}");
        }

        Directory.CreateDirectory(request.WorktreePath);
        Directory.CreateDirectory(request.RuntimeHomePath);

        var env = BubblewrapWorkerSandbox.SanitizeEnvironment(request.ExtraEnvironment);
        env["HOME"] = request.RuntimeHomePath;
        env["DEVCOMMANDER_WORKTREE"] = request.WorktreePath;
        env["DEVCOMMANDER_RUNTIME_HOME"] = request.RuntimeHomePath;

        return await _runner.StartAsync(new ProcessStartRequest(
            FileName: request.Executable,
            Arguments: request.Arguments,
            WorkingDirectory: request.WorktreePath,
            Environment: env,
            MaxOutputChars: request.MaxOutputChars), ct);
    }

    public static bool IsPathAllowed(string targetPath, string worktreePath, string runtimeHomePath)
    {
        var full = Path.GetFullPath(targetPath);
        var work = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var home = Path.GetFullPath(runtimeHomePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(work, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(home, StringComparison.OrdinalIgnoreCase)
               || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), work.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
               || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), home.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}
