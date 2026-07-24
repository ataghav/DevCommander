using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevCommander.Domain;

namespace DevCommander.HyperCare;

/// <summary>Session configuration loaded from {DataRoot}/hypercare/config.json (FR-HC-005).</summary>
public sealed record HyperCareConfig
{
    public int MaxConcurrency { get; init; } = 1;
    public decimal BudgetUsd { get; init; }

    /// <summary>Session-budget slice reserved per claimed fix track; also the track's mission budget.</summary>
    public decimal FixTrackBudgetUsd { get; init; } = 1.0m;

    public decimal TriageEstimateUsd { get; init; } = 0.05m;
    public decimal InvestigateEstimateUsd { get; init; } = 0.10m;
    public string DefaultSeverity { get; init; } = "medium";
    public int DefaultPriority { get; init; }
    public int PollIntervalSeconds { get; init; } = 60;
    public int TriageContextMaxChars { get; init; } = 32_768;

    /// <summary>True ⇒ redaction patterns are mandatory (FR-HC-003 prod profile).</summary>
    public bool Production { get; init; }

    public RedactionConfig Redaction { get; init; } = new();
    public GrafanaConfig Grafana { get; init; } = new();
    public IReadOnlyList<ServiceConfig> Services { get; init; } = [];

    public HyperCareSeverity ParsedDefaultSeverity =>
        Enum.TryParse<HyperCareSeverity>(DefaultSeverity, ignoreCase: true, out var s) ? s : HyperCareSeverity.Medium;
}

public sealed record RedactionConfig
{
    public IReadOnlyList<string> Patterns { get; init; } = [];
}

public sealed record GrafanaConfig
{
    public string BaseUrl { get; init; } = "";

    /// <summary>Name of the env var holding the Grafana token — never the token itself.</summary>
    public string TokenEnvVar { get; init; } = "";
}

public sealed record ServiceConfig
{
    public string Id { get; init; } = "";
    public string RepoId { get; init; } = "";
    public bool Enabled { get; init; } = true;

    /// <summary>Optional coder runtime override; default = repo.DefaultRuntime.</summary>
    public string? CoderRuntime { get; init; }

    public IReadOnlyList<GrafanaQueryConfig> GrafanaQueries { get; init; } = [];
    public IReadOnlyList<AzureCheckConfig> AzureChecks { get; init; } = [];

    /// <summary>Imperative include regexes — a line must match at least one to become a candidate.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>Imperative exclude regexes — any match drops the line before triage (FR-HC-011).</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

/// <summary>
/// Generic Grafana HTTP request template. {fromMs}/{toMs} are replaced with the poll window
/// (unix millis). Every string leaf of the response JSON becomes a candidate line.
/// </summary>
public sealed record GrafanaQueryConfig
{
    public string Name { get; init; } = "";
    public string Method { get; init; } = "POST";
    public string Path { get; init; } = "api/ds/query";
    public string? BodyTemplate { get; init; }
}

/// <summary>Host `az` CLI check: candidate when exit != 0 or output fails ExpectRegex.</summary>
public sealed record AzureCheckConfig
{
    public string Name { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? ExpectRegex { get; init; }
}

public sealed record HyperCareConfigLoadResult(
    HyperCareConfig? Config,
    string RawJson,
    string Sha256,
    IReadOnlyList<string> Problems);

public static class HyperCareConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static HyperCareConfigLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return new(null, "", "", [$"Config file not found at '{path}'."]);
        }

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new(null, "", "", [$"Config file at '{path}' is unreadable: {ex.Message}"]);
        }

        return Parse(raw, path);
    }

    public static HyperCareConfigLoadResult Parse(string raw, string origin)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        try
        {
            var config = JsonSerializer.Deserialize<HyperCareConfig>(raw, JsonOptions);
            return config is null
                ? new(null, raw, hash, [$"Config at '{origin}' deserialized to null."])
                : new(config, raw, hash, []);
        }
        catch (JsonException ex)
        {
            return new(null, raw, hash, [$"Config at '{origin}' is not valid JSON: {ex.Message}"]);
        }
    }
}
