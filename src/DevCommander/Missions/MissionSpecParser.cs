using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevCommander.Domain;

namespace DevCommander.Missions;

public sealed record MissionSpecDocument(
    IReadOnlyList<string> Repositories,
    string Goal,
    string InScope,
    string OutOfScope,
    IReadOnlyDictionary<string, VerificationSection> VerificationCommands,
    string AcceptanceCriteria,
    RuntimePreference RuntimePreference,
    string RawContent,
    string ContentHash);

public sealed record VerificationSection(bool UseRepoDefault, IReadOnlyList<string> Commands);

public sealed record RuntimePreference(RuntimeKind Default, IReadOnlyDictionary<string, RuntimeKind> RepoOverrides);

public sealed record MissionPlan(IReadOnlyList<PlannedTask> Tasks);

public sealed record PlannedTask(string RepoId, int Phase, string Description);

public sealed record MissionValidationResult(bool IsValid, IReadOnlyList<string> Problems, MissionSpecDocument? Spec);

public static class MissionSpecParser
{
    private static readonly string[] RequiredSections =
    [
        "Repositories",
        "Goal",
        "In-scope",
        "Out-of-scope",
        "Verification commands",
        "Acceptance criteria",
        "Runtime preference",
    ];

    public static MissionValidationResult ParseAndValidate(string content, IReadOnlySet<string> registeredRepoIds)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return new MissionValidationResult(false, ["Mission file is empty."], null);
        }

        var sections = SplitSections(content);
        foreach (var required in RequiredSections)
        {
            if (!sections.TryGetValue(required, out var body) || string.IsNullOrWhiteSpace(body))
            {
                problems.Add($"Missing or empty section: {required}");
            }
        }

        if (problems.Count > 0)
        {
            // Name every problem even when some sections are present.
            foreach (var required in RequiredSections)
            {
                if (!sections.ContainsKey(required) && problems.All(p => !p.Contains(required, StringComparison.Ordinal)))
                {
                    problems.Add($"Missing or empty section: {required}");
                }
            }

            return new MissionValidationResult(false, problems.Distinct().ToList(), null);
        }

        var repos = ParseBulletList(sections["Repositories"]);
        if (repos.Count == 0)
        {
            problems.Add("Repositories section must list at least one repository.");
        }

        foreach (var repo in repos)
        {
            if (!registeredRepoIds.Contains(repo))
            {
                problems.Add($"Unknown repository id: {repo}");
            }
        }

        var verification = ParseVerification(sections["Verification commands"], problems);
        foreach (var repo in repos)
        {
            if (!verification.ContainsKey(repo))
            {
                problems.Add($"Missing verification subsection for repository: {repo}");
            }
        }

        foreach (var key in verification.Keys)
        {
            if (!repos.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add($"Unknown verification subsection (not listed in Repositories): {key}");
            }
        }

        var runtimePref = ParseRuntimePreference(sections["Runtime preference"], problems);
        if (runtimePref is not null)
        {
            foreach (var key in runtimePref.RepoOverrides.Keys)
            {
                if (!repos.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"Unknown runtime preference override (not listed in Repositories): {key}");
                }
            }
        }

        if (problems.Count > 0)
        {
            return new MissionValidationResult(false, problems, null);
        }

        var normalized = content.Replace("\r\n", "\n");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var doc = new MissionSpecDocument(
            Repositories: repos,
            Goal: sections["Goal"].Trim(),
            InScope: sections["In-scope"].Trim(),
            OutOfScope: sections["Out-of-scope"].Trim(),
            VerificationCommands: verification,
            AcceptanceCriteria: sections["Acceptance criteria"].Trim(),
            RuntimePreference: runtimePref!,
            RawContent: normalized,
            ContentHash: hash);

        return new MissionValidationResult(true, [], doc);
    }

    public static IReadOnlyList<string> ValidatePlan(MissionPlan plan, MissionSpecDocument spec, IReadOnlySet<string> registeredRepoIds)
    {
        var problems = new List<string>();
        if (plan.Tasks is null || plan.Tasks.Count == 0)
        {
            problems.Add("Plan must contain at least one task (zero-task plans are rejected).");
            return problems;
        }

        var listed = new HashSet<string>(spec.Repositories, StringComparer.OrdinalIgnoreCase);
        var seenRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phases = new SortedSet<int>();

        foreach (var task in plan.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Description))
            {
                problems.Add("Plan contains a task with empty description.");
            }

            if (string.IsNullOrWhiteSpace(task.RepoId))
            {
                problems.Add("Plan contains a task with empty repoId.");
                continue;
            }

            if (!registeredRepoIds.Contains(task.RepoId))
            {
                problems.Add($"Plan references unknown repository: {task.RepoId}");
            }

            if (!listed.Contains(task.RepoId))
            {
                problems.Add($"Plan references repository not listed in mission: {task.RepoId}");
            }

            if (task.Phase <= 0)
            {
                problems.Add($"Plan has non-positive phase for {task.RepoId}: {task.Phase}");
            }
            else
            {
                phases.Add(task.Phase);
            }

            seenRepos.Add(task.RepoId);
        }

        foreach (var repo in listed)
        {
            if (!seenRepos.Contains(repo))
            {
                problems.Add($"Plan omits listed repository: {repo}");
            }
        }

        if (phases.Count > 0)
        {
            var expected = 1;
            foreach (var p in phases)
            {
                if (p != expected)
                {
                    problems.Add($"Phases must be positive and contiguous starting at 1; missing phase {expected}.");
                    break;
                }

                expected++;
            }
        }

        return problems;
    }

    public static RuntimeKind SelectRuntime(MissionSpecDocument spec, string repoId, RuntimeKind repoDefault)
    {
        if (spec.RuntimePreference.RepoOverrides.TryGetValue(repoId, out var overrideRt))
        {
            return overrideRt;
        }

        return spec.RuntimePreference.Default;
    }

    public static IReadOnlyList<string> ResolveVerifyCommands(MissionSpecDocument spec, string repoId, IReadOnlyList<string> repoDefaults)
    {
        if (!spec.VerificationCommands.TryGetValue(repoId, out var section))
        {
            return repoDefaults;
        }

        return section.UseRepoDefault ? repoDefaults : section.Commands;
    }

    private static Dictionary<string, string> SplitSections(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        string? current = null;
        var body = new StringBuilder();

        void Flush()
        {
            if (current is not null)
            {
                result[current] = body.ToString().Trim();
            }

            body.Clear();
        }

        foreach (var line in lines)
        {
            var heading = TryParseHeading(line);
            if (heading is not null)
            {
                Flush();
                current = heading;
                continue;
            }

            if (current is not null)
            {
                body.AppendLine(line);
            }
        }

        Flush();
        return result;
    }

    private static string? TryParseHeading(string line)
    {
        var m = Regex.Match(line.Trim(), @"^##\s+(.+)$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static List<string> ParseBulletList(string body)
    {
        var list = new List<string>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(line, @"^[-*]\s+(.+)$");
            if (m.Success)
            {
                list.Add(m.Groups[1].Value.Trim());
            }
        }

        return list;
    }

    private static Dictionary<string, VerificationSection> ParseVerification(string body, List<string> problems)
    {
        var map = new Dictionary<string, VerificationSection>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var commands = new List<string>();
        var useDefault = false;

        void Flush()
        {
            if (current is null)
            {
                return;
            }

            if (!useDefault && commands.Count == 0)
            {
                problems.Add($"Verification subsection '{current}' is empty.");
            }
            else
            {
                map[current] = new VerificationSection(useDefault, commands.ToList());
            }

            commands.Clear();
            useDefault = false;
        }

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            var sub = Regex.Match(trimmed, @"^###\s+(.+)$");
            if (sub.Success)
            {
                Flush();
                current = sub.Groups[1].Value.Trim();
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (string.Equals(trimmed, "repo default", StringComparison.OrdinalIgnoreCase))
            {
                useDefault = true;
                continue;
            }

            var bullet = Regex.Match(trimmed, @"^[-*]\s+(.+)$");
            if (bullet.Success)
            {
                commands.Add(bullet.Groups[1].Value.Trim());
            }
        }

        Flush();
        return map;
    }

    private static RuntimePreference? ParseRuntimePreference(string body, List<string> problems)
    {
        RuntimeKind? def = null;
        var overrides = new Dictionary<string, RuntimeKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(line, @"^(default|[A-Za-z0-9_.-]+)\s*:\s*(\w+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                continue;
            }

            if (!TryParseRuntime(m.Groups[2].Value, out var kind))
            {
                problems.Add($"Unknown runtime: {m.Groups[2].Value}");
                continue;
            }

            if (m.Groups[1].Value.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                def = kind;
            }
            else
            {
                overrides[m.Groups[1].Value] = kind;
            }
        }

        if (def is null)
        {
            problems.Add("Runtime preference must include 'default: {runtime}'.");
            return null;
        }

        return new RuntimePreference(def.Value, overrides);
    }

    public static bool TryParseRuntime(string value, out RuntimeKind kind)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "claude":
                kind = RuntimeKind.Claude;
                return true;
            case "codex":
                kind = RuntimeKind.Codex;
                return true;
            case "cursor":
            case "agent":
                kind = RuntimeKind.Cursor;
                return true;
            case "opencode":
            case "open-code":
                kind = RuntimeKind.OpenCode;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
