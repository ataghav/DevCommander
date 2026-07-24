namespace DevCommander.HyperCare.Watching;

public interface IGrafanaClient
{
    /// <summary>Returns null on success, or a human-readable problem description.</summary>
    Task<string?> CheckHealthAsync(string baseUrl, string token, CancellationToken ct);

    /// <summary>Executes a query template over [from, to] and returns the raw response JSON.</summary>
    Task<string> QueryAsync(
        string baseUrl,
        string token,
        GrafanaQueryConfig query,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
}

public sealed class GrafanaClient(IHttpClientFactory httpClientFactory) : IGrafanaClient
{
    public async Task<string?> CheckHealthAsync(string baseUrl, string token, CancellationToken ct)
    {
        try
        {
            using var client = CreateClient(token, TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(Combine(baseUrl, "api/health"), ct);
            return response.IsSuccessStatusCode
                ? null
                : $"Grafana health check returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"Grafana unreachable: {ex.Message}";
        }
    }

    public async Task<string> QueryAsync(
        string baseUrl,
        string token,
        GrafanaQueryConfig query,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        using var client = CreateClient(token, TimeSpan.FromSeconds(30));
        var url = Combine(baseUrl, Substitute(query.Path, from, to));
        using var request = new HttpRequestMessage(new HttpMethod(query.Method), url);
        if (query.BodyTemplate is { Length: > 0 } body)
        {
            request.Content = new StringContent(
                Substitute(body, from, to), System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private HttpClient CreateClient(string token, TimeSpan timeout)
    {
        var client = httpClientFactory.CreateClient("grafana");
        client.Timeout = timeout;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static string Combine(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static string Substitute(string template, DateTimeOffset from, DateTimeOffset to) =>
        template
            .Replace("{fromMs}", from.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{toMs}", to.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
}
