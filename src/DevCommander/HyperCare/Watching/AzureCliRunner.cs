using System.Text.RegularExpressions;
using DevCommander.Process;

namespace DevCommander.HyperCare.Watching;

public interface IAzureCliRunner
{
    /// <summary>Returns null on success, or a problem description ("az account show" identity probe).</summary>
    Task<string?> CheckIdentityAsync(CancellationToken ct);

    /// <summary>Runs one configured check. Ok=false ⇒ Evidence is the candidate line payload.</summary>
    Task<(bool Ok, string Evidence)> RunCheckAsync(AzureCheckConfig check, CancellationToken ct);
}

public sealed class AzureCliRunner(IProcessRunner processRunner) : IAzureCliRunner
{
    public async Task<string?> CheckIdentityAsync(CancellationToken ct)
    {
        try
        {
            var completion = await RunAsync(["account", "show", "-o", "none"], TimeSpan.FromSeconds(30), ct);
            return completion.ExitCode == 0
                ? null
                : $"Azure CLI identity unusable (az account show exit {completion.ExitCode}): {Trim(completion.StdErr)}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"Azure CLI unavailable: {ex.Message}";
        }
    }

    public async Task<(bool Ok, string Evidence)> RunCheckAsync(AzureCheckConfig check, CancellationToken ct)
    {
        var completion = await RunAsync(check.Args, TimeSpan.FromSeconds(60), ct);
        var output = string.IsNullOrWhiteSpace(completion.StdOut) ? completion.StdErr : completion.StdOut;
        if (completion.ExitCode != 0)
        {
            return (false, $"{check.Name}: az exited {completion.ExitCode}: {Trim(output)}");
        }

        if (check.ExpectRegex is { Length: > 0 } expect && !Regex.IsMatch(output.Trim(), expect))
        {
            return (false, $"{check.Name}: output '{Trim(output)}' did not match expected '{expect}'");
        }

        return (true, "");
    }

    private async Task<ProcessCompletion> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["HOME"] = Environment.GetEnvironmentVariable("HOME"),
            ["USERPROFILE"] = Environment.GetEnvironmentVariable("USERPROFILE"),
            ["AZURE_CONFIG_DIR"] = Environment.GetEnvironmentVariable("AZURE_CONFIG_DIR"),
        };

        var exec = await processRunner.StartAsync(new ProcessStartRequest(
            FileName: "az",
            Arguments: args,
            WorkingDirectory: Environment.CurrentDirectory,
            Environment: env), ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await exec.Completion.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await exec.KillTreeAsync(CancellationToken.None);
            throw new TimeoutException($"az {string.Join(' ', args)} timed out after {timeout.TotalSeconds:0}s.");
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
