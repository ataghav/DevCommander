using System.Text.RegularExpressions;
using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.HyperCare.Watching;
using DevCommander.Missions;
using DevCommander.Runtimes;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.HyperCare;

public interface IHyperCareActivationValidator
{
    /// <summary>Fail-closed validation per FR-HC-003: returns every failing check by name.</summary>
    Task<IReadOnlyList<string>> ValidateAsync(HyperCareConfig config, CancellationToken ct);
}

public sealed class HyperCareActivationValidator(
    IDbContextFactory<AppDbContext> dbFactory,
    IGrafanaClient grafana,
    IAzureCliRunner azure,
    IGitHubCli gitHub,
    IRuntimeRegistry runtimes) : IHyperCareActivationValidator
{
    public async Task<IReadOnlyList<string>> ValidateAsync(HyperCareConfig config, CancellationToken ct)
    {
        var problems = new List<string>();

        if (config.Services.Count == 0)
        {
            problems.Add("Service list is empty.");
        }
        else if (!config.Services.Any(s => s.Enabled))
        {
            problems.Add("All services are disabled; at least one enabled service is required.");
        }

        foreach (var service in config.Services.Where(s => s.Enabled))
        {
            if (service.GrafanaQueries.Count == 0 && service.AzureChecks.Count == 0)
            {
                problems.Add($"Service '{service.Id}' has no watch targets (needs a Grafana query or an Azure check).");
            }

            if (service.GrafanaQueries.Count > 0 && service.Include.Count == 0)
            {
                problems.Add($"Service '{service.Id}' has Grafana queries but no include filters; nothing would match.");
            }

            // Hyper-Care never mutates cloud state (FR-HC-045 / NFR-HC-08): az checks must be read-only.
            foreach (var check in service.AzureChecks.Where(c => !IsReadOnlyAzureCheck(c)))
            {
                problems.Add($"Service '{service.Id}' azure check '{check.Name}' is not a read-only command "
                    + "(the verb must be or start with show/list/get/check).");
            }
        }

        var duplicateIds = config.Services
            .GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        foreach (var id in duplicateIds)
        {
            problems.Add($"Duplicate service id '{id}'.");
        }

        foreach (var service in config.Services.Where(s => string.IsNullOrWhiteSpace(s.Id)))
        {
            problems.Add("A service has a missing/blank id.");
        }

        if (config.MaxConcurrency < 1)
        {
            problems.Add($"maxConcurrency must be >= 1 (got {config.MaxConcurrency}).");
        }

        if (config.BudgetUsd <= 0)
        {
            problems.Add($"budgetUsd must be positive (got {config.BudgetUsd}).");
        }

        if (config.FixTrackBudgetUsd <= 0 || config.FixTrackBudgetUsd > config.BudgetUsd)
        {
            problems.Add($"fixTrackBudgetUsd must be positive and <= budgetUsd (got {config.FixTrackBudgetUsd}).");
        }

        if (config.TriageEstimateUsd <= 0 || config.InvestigateEstimateUsd <= 0)
        {
            problems.Add("triageEstimateUsd and investigateEstimateUsd must be positive.");
        }

        if (config.PollIntervalSeconds < 5)
        {
            problems.Add($"pollIntervalSeconds must be >= 5 (got {config.PollIntervalSeconds}).");
        }

        if (config.TriageContextMaxChars < 256)
        {
            problems.Add($"triageContextMaxChars must be >= 256 (got {config.TriageContextMaxChars}).");
        }

        if (!Enum.TryParse<HyperCareSeverity>(config.DefaultSeverity, ignoreCase: true, out _))
        {
            problems.Add($"defaultSeverity '{config.DefaultSeverity}' is not one of low|medium|high|critical.");
        }

        if (config.Production && config.Redaction.Patterns.Count == 0)
        {
            problems.Add("production=true requires at least one redaction pattern.");
        }

        ValidateRegexes(config, problems);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await ValidateRepoMappingAsync(db, config, problems, ct);
        await ValidateExclusivityAsync(db, problems, ct);
        await ValidateEffectiveRuntimesAsync(db, config, problems, ct);
        await ValidateGrafanaAsync(config, problems, ct);

        if (config.Services.Any(s => s.Enabled && s.AzureChecks.Count > 0)
            && await azure.CheckIdentityAsync(ct) is { } azProblem)
        {
            problems.Add(azProblem);
        }

        if (await gitHub.CheckAuthAsync(ct) is { } ghProblem)
        {
            problems.Add(ghProblem);
        }

        return problems;
    }

    /// <summary>The az verb is the last positional token before the first option (az &lt;group…&gt; &lt;verb&gt; [options]).</summary>
    private static bool IsReadOnlyAzureCheck(AzureCheckConfig check)
    {
        var positional = check.Args.TakeWhile(a => !a.StartsWith('-')).ToList();
        if (positional.Count == 0)
        {
            return false;
        }

        var verb = positional[^1];
        return new[] { "show", "list", "get", "check" }
            .Any(allowed => verb.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                || verb.StartsWith(allowed + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateRegexes(HyperCareConfig config, List<string> problems)
    {
        foreach (var (pattern, origin) in
            config.Redaction.Patterns.Select(p => (p, "redaction"))
            .Concat(config.Services.SelectMany(s =>
                s.Include.Select(p => (p, $"service '{s.Id}' include"))
                .Concat(s.Exclude.Select(p => (p, $"service '{s.Id}' exclude")))
                .Concat(s.AzureChecks.Where(c => c.ExpectRegex is { Length: > 0 })
                    .Select(c => (c.ExpectRegex!, $"service '{s.Id}' azure check '{c.Name}' expectRegex"))))))
        {
            try
            {
                _ = new Regex(pattern);
            }
            catch (ArgumentException)
            {
                problems.Add($"Invalid {origin} regex: '{pattern}'.");
            }
        }
    }

    private static async Task ValidateRepoMappingAsync(
        AppDbContext db, HyperCareConfig config, List<string> problems, CancellationToken ct)
    {
        // Exact-case matching: downstream EF lookups compare repo ids ordinally.
        var registered = await db.Repos.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
        var registeredSet = new HashSet<string>(registered, StringComparer.Ordinal);
        foreach (var service in config.Services)
        {
            if (string.IsNullOrWhiteSpace(service.RepoId))
            {
                problems.Add($"Service '{service.Id}' must map to exactly one repoId (got none).");
            }
            else if (!registeredSet.Contains(service.RepoId))
            {
                problems.Add($"Service '{service.Id}' maps to unknown repoId '{service.RepoId}' (repo ids are case-sensitive).");
            }
        }
    }

    private static async Task ValidateExclusivityAsync(AppDbContext db, List<string> problems, CancellationToken ct)
    {
        // Fail-closed exclusivity (BR-HC-001): activation refuses while parent missions execute.
        var active = await db.Missions.AsNoTracking()
            .Where(m => m.Status == MissionStatus.Starting
                || m.Status == MissionStatus.Running)
            .Select(m => m.Slug)
            .ToListAsync(ct);
        foreach (var slug in active)
        {
            problems.Add($"Parent mission '{slug}' is active; clear Normal-mode work before activating Hyper-Care.");
        }
    }

    private async Task ValidateEffectiveRuntimesAsync(
        AppDbContext db, HyperCareConfig config, List<string> problems, CancellationToken ct)
    {
        var repoRuntimes = await db.Repos.AsNoTracking()
            .ToDictionaryAsync(r => r.Id, r => r.DefaultRuntime, StringComparer.Ordinal, ct);
        foreach (var service in config.Services.Where(s => s.Enabled))
        {
            RuntimeKind effective;
            if (service.CoderRuntime is { Length: > 0 } preferred)
            {
                if (!MissionSpecParser.TryParseRuntime(preferred, out effective))
                {
                    problems.Add($"Service '{service.Id}' has unknown coderRuntime '{service.CoderRuntime}'.");
                    continue;
                }
            }
            else if (!repoRuntimes.TryGetValue(service.RepoId ?? "", out effective))
            {
                continue; // Unknown repo already reported by the mapping check.
            }

            if (!runtimes.IsAvailable(effective))
            {
                problems.Add($"Service '{service.Id}' coder runtime '{effective}' is unavailable: "
                    + (runtimes.UnavailableReason(effective) ?? "unknown reason"));
            }
        }
    }

    private async Task ValidateGrafanaAsync(HyperCareConfig config, List<string> problems, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Grafana.BaseUrl)
            || !Uri.TryCreate(config.Grafana.BaseUrl, UriKind.Absolute, out _))
        {
            problems.Add($"grafana.baseUrl '{config.Grafana.BaseUrl}' must be an absolute URL.");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Grafana.TokenEnvVar))
        {
            problems.Add("grafana.tokenEnvVar is required.");
            return;
        }

        var token = Environment.GetEnvironmentVariable(config.Grafana.TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
        {
            problems.Add($"Grafana token env var '{config.Grafana.TokenEnvVar}' is not set.");
            return;
        }

        if (await grafana.CheckHealthAsync(config.Grafana.BaseUrl, token, ct) is { } healthProblem)
        {
            problems.Add(healthProblem);
        }
    }
}
