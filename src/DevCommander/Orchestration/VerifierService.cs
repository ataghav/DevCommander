using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevCommander.Domain.Entities;
using DevCommander.Process;

namespace DevCommander.Orchestration;

public sealed record VerificationResult(
    bool Succeeded,
    bool NeedsApproval,
    int? CommandIndex,
    string? Command,
    int? ExitCode,
    string Evidence,
    string? ApprovalCommandHash = null);

public interface IVerifierService
{
    Task<VerificationResult> VerifyAsync(
        Repo repo,
        IReadOnlyList<string> effectiveCommands,
        string worktreePath,
        Func<int, string, string, CancellationToken, Task<bool>> approvalAllowed,
        CancellationToken ct,
        int? resumeFromIndex = null);
}

public sealed class VerifierService(IProcessRunner processRunner) : IVerifierService
{
    public async Task<VerificationResult> VerifyAsync(
        Repo repo,
        IReadOnlyList<string> effectiveCommands,
        string worktreePath,
        Func<int, string, string, CancellationToken, Task<bool>> approvalAllowed,
        CancellationToken ct,
        int? resumeFromIndex = null)
    {
        var start = resumeFromIndex ?? 0;
        for (var index = start; index < effectiveCommands.Count; index++)
        {
            var command = effectiveCommands[index];
            var normalized = Normalize(command);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            var gated = repo.GetGatedOps().Any(pattern =>
                normalized.Contains(Normalize(pattern), StringComparison.OrdinalIgnoreCase));
            if (gated && !await approvalAllowed(index, command, hash, ct))
            {
                return new(false, true, index, command, null, "Approval required.", hash);
            }

            var (fileName, args) = OperatingSystem.IsWindows()
                ? ("cmd.exe", (IReadOnlyList<string>)["/d", "/s", "/c", command])
                : ("/bin/sh", (IReadOnlyList<string>)["-c", command]);
            var execution = await processRunner.StartAsync(new ProcessStartRequest(
                fileName, args, worktreePath,
                new Dictionary<string, string?> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") }), ct);
            var completion = await execution.Completion.WaitAsync(ct);
            var evidence = Trim($"$ {command}\n{completion.StdOut}\n{completion.StdErr}");
            if (completion.ExitCode != 0)
            {
                return new(false, false, index, command, completion.ExitCode, evidence, gated ? hash : null);
            }
        }

        return new(true, false, null, null, 0, "All verification commands passed.");
    }

    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
    private static string Trim(string value) => value.Length <= 8000 ? value : value[..8000] + "…";
}
