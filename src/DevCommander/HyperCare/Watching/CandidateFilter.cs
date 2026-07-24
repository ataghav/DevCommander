using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevCommander.HyperCare.Watching;

/// <summary>
/// Pure imperative pre-LLM pipeline (FR-HC-011 / BR-HC-010): extract → redact → filter →
/// group by cheap local signature → bound context. The firehose never reaches the LLM.
/// </summary>
public static partial class CandidateFilter
{
    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b|\b[0-9a-fA-F]{16,}\b")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    /// <summary>Every string leaf of a JSON document (Grafana responses carry log lines as string leaves).</summary>
    public static IReadOnlyList<string> ExtractStringLeaves(string json)
    {
        var lines = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, lines);
        }
        catch (JsonException)
        {
            // Non-JSON payload: treat each line as a candidate line.
            lines.AddRange(json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return lines;

        static void Walk(JsonElement element, List<string> sink)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    if (element.GetString() is { Length: > 0 } s)
                    {
                        sink.Add(s);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Walk(property.Value, sink);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, sink);
                    }

                    break;
            }
        }
    }

    /// <summary>Include wins only when no exclude matches; empty include list matches nothing (explicit criteria only).</summary>
    public static IReadOnlyList<string> Apply(
        IEnumerable<string> lines,
        IReadOnlyList<Regex> include,
        IReadOnlyList<Regex> exclude) =>
        lines.Where(line =>
                include.Any(r => r.IsMatch(line))
                && !exclude.Any(r => r.IsMatch(line)))
            .ToList();

    /// <summary>Applied before anything is persisted or sent to an LLM.</summary>
    public static string Redact(string line, IReadOnlyList<Regex> patterns) =>
        patterns.Aggregate(line, (current, pattern) => pattern.Replace(current, "[REDACTED]"));

    /// <summary>Cheap pre-LLM grouping key: volatile values collapsed, then hashed.</summary>
    public static string LocalSignature(string line)
    {
        var normalized = GuidPattern().Replace(line, "<guid>");
        normalized = HexPattern().Replace(normalized, "<hex>");
        normalized = DigitsPattern().Replace(normalized, "<n>");
        normalized = WhitespacePattern().Replace(normalized, " ").Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>Distinct sample lines joined under a hard char cap (NFR-HC-09).</summary>
    public static string BuildBoundedContext(IEnumerable<string> lines, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var line in lines.Distinct())
        {
            if (sb.Length + line.Length + 1 > maxChars)
            {
                break;
            }

            sb.AppendLine(line);
        }

        return sb.Length == 0 ? lines.FirstOrDefault()?[..Math.Min(maxChars, lines.First().Length)] ?? "" : sb.ToString();
    }

    public static IReadOnlyList<Regex> CompileAll(IReadOnlyList<string> patterns) =>
        patterns.Select(p => new Regex(p, RegexOptions.None, TimeSpan.FromSeconds(1))).ToList();
}
