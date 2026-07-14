using DevCommander.Domain;
using DevCommander.Options;
using DevCommander.Runtimes;
using DevCommander.Sandbox;
using Microsoft.Extensions.Options;

namespace DevCommander.Services;

public sealed class RuntimeCapabilityProbeHostedService(
    IWorkerSandbox sandbox,
    RuntimeRegistry registry,
    IOptions<DevCommanderOptions> options,
    ILogger<RuntimeCapabilityProbeHostedService> logger) : IHostedService
{
    private readonly DevCommanderOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await sandbox.ProbeAsync(cancellationToken);
        foreach (var kind in Enum.GetValues<RuntimeKind>())
        {
            if (!sandbox.IsAvailable)
            {
                registry.MarkUnavailable(kind, sandbox.UnavailableReason ?? "worker sandbox unavailable");
                continue;
            }

            var executable = GetExecutable(kind);
            if (FindExecutable(executable) is null)
            {
                registry.MarkUnavailable(kind, $"executable not found: {executable}");
                continue;
            }

            registry.MarkAvailable(kind);
        }

        logger.LogInformation("Runtime capability probe completed");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string GetExecutable(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Claude => _options.Runtimes.Claude.Executable,
        RuntimeKind.Codex => _options.Runtimes.Codex.Executable,
        RuntimeKind.Cursor => _options.Runtimes.Cursor.Executable,
        RuntimeKind.OpenCode => _options.Runtimes.OpenCode.Executable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string? FindExecutable(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar))
        {
            return File.Exists(executable) ? executable : null;
        }

        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(path, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
            {
                return candidate + ".exe";
            }
        }

        return null;
    }
}
