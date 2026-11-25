using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace TokenUsageReport.Reporting;

public sealed class ArmException : Exception
{
    public HttpStatusCode Status { get; }
    public ArmException(HttpStatusCode status, string message) : base(message) => Status = status;
}

/// <summary>Minimal ARM / Resource Graph / data-plane REST client with paging and throttling retries.</summary>
public sealed class ArmClient
{
    public const string ArmScope = "https://management.azure.com/.default";
    public const string FoundryScope = "https://ai.azure.com/.default";
    private const string GraphUrl = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01";

    private readonly TokenCredential _credential;
    private readonly HttpClient _http;

    public ArmClient(TokenCredential credential, HttpClient? http = null)
    {
        _credential = credential;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string url, string scope, object? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(text)) return default;
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }

            var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            if (transient && attempt < 4)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 2);
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct);
                continue;
            }
            throw new ArmException(response.StatusCode, Summarize(response.StatusCode, text));
        }
    }

    public Task<JsonElement> GetAsync(string url, CancellationToken ct, string scope = ArmScope) =>
        SendAsync(HttpMethod.Get, url, scope, null, ct);

    /// <summary>Follows ARM-style "nextLink" paging and returns every item of the "value" array.</summary>
    public async Task<List<JsonElement>> ListAsync(string url, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? next = url;
        while (!string.IsNullOrEmpty(next))
        {
            var page = await GetAsync(next, ct);
            if (page.ValueKind == JsonValueKind.Object && page.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                items.AddRange(value.EnumerateArray());
            next = page.ValueKind == JsonValueKind.Object && page.TryGetProperty("nextLink", out var nl) && nl.ValueKind == JsonValueKind.String
                ? nl.GetString() : null;
        }
        return items;
    }

    /// <summary>Runs an Azure Resource Graph query over the given subscriptions, following $skipToken paging.</summary>
    public async Task<List<JsonElement>> GraphAsync(string query, IReadOnlyCollection<string> subscriptions, CancellationToken ct)
    {
        var rows = new List<JsonElement>();
        if (subscriptions.Count == 0) return rows;
        string? skipToken = null;
        do
        {
            var options = new Dictionary<string, object> { ["$top"] = 1000 };
            if (skipToken is not null) options["$skipToken"] = skipToken;
            var result = await SendAsync(HttpMethod.Post, GraphUrl, ArmScope, new { subscriptions, query, options }, ct);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                rows.AddRange(data.EnumerateArray());
            skipToken = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("$skipToken", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString() : null;
        }
        while (!string.IsNullOrEmpty(skipToken));
        return rows;
    }

    private static string Summarize(HttpStatusCode status, string body)
    {
        var message = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) ? e : root;
            if (error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) ? c.ToString() : "";
                var msg = error.TryGetProperty("message", out var m) ? m.ToString() : "";
                message = $"{code} {msg}".Trim();
            }
        }
        catch (JsonException) { /* keep raw body */ }
        if (message.Length > 300) message = message[..300] + "...";
        return $"HTTP {(int)status}: {message}";
    }
}

/// <summary>Wraps a credential with a short in-memory token cache (CLI credentials spawn a process per token).</summary>
public sealed class CachedCredential : TokenCredential
{
    private readonly TokenCredential _inner;
    private readonly Dictionary<string, AccessToken> _cache = new();

    public CachedCredential(TokenCredential inner) => _inner = inner;

    public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) =>
        GetTokenAsync(context, ct).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
    {
        var key = string.Join(' ', context.Scopes) + "|" + context.TenantId;
        lock (_cache)
            if (_cache.TryGetValue(key, out var hit) && hit.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5)) return hit;
        var token = await _inner.GetTokenAsync(context, ct);
        lock (_cache) _cache[key] = token;
        return token;
    }
}
