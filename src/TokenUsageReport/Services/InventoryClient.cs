using System.Text;
using System.Text.Json;
using Azure.Core;
using TokenUsageReport.Models;

namespace TokenUsageReport.Services;

/// <summary>
/// Builds the Cognitive Services deployment inventory used to enrich token rows.
/// Uses Azure Resource Graph to list accounts, then the ARM REST API to list each
/// account's deployments. Authenticated with the app's managed identity.
/// </summary>
public sealed class InventoryClient
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ResourceGraphUri =
        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01";

    private const string AccountsQuery =
        "Resources | where type =~ 'microsoft.cognitiveservices/accounts' " +
        "| where kind in~ ('OpenAI', 'AIServices', 'CognitiveServices') " +
        "| project AccountId = id, AccountName = name, AccountEndpoint = properties.endpoint, SubscriptionId = subscriptionId";

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;

    public InventoryClient(TokenCredential credential, HttpClient? http = null)
    {
        _credential = credential;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<Dictionary<string, InventoryItem>> BuildInventoryAsync(CancellationToken ct)
    {
        var inventory = new Dictionary<string, InventoryItem>();
        var accounts = await GetAccountsAsync(ct);

        foreach (var account in accounts)
        {
            List<JsonElement> deployments;
            try
            {
                deployments = await GetDeploymentsAsync(account.AccountId, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: failed to list deployments for {account.AccountName}: {ex.Message}");
                continue;
            }

            var backend = NormalizeEndpoint(account.AccountEndpoint);
            foreach (var d in deployments)
            {
                var deploymentName = GetProp(d, "name");
                var item = new InventoryItem
                {
                    Backend = backend,
                    DeploymentName = deploymentName,
                    ModelName = GetNested(d, "properties", "model", "name"),
                    SkuName = GetNested(d, "sku", "name"),
                    SkuCapacity = GetNested(d, "sku", "capacity"),
                    AccountName = account.AccountName,
                    CogSvcSubscriptionId = account.SubscriptionId,
                    AccountId = account.AccountId,
                };
                inventory[item.Key] = item;
            }
        }

        return inventory;
    }

    private async Task<List<AccountInfo>> GetAccountsAsync(CancellationToken ct)
    {
        var accounts = new List<AccountInfo>();
        string? skipToken = null;

        do
        {
            var options = new Dictionary<string, object> { ["$top"] = 1000 };
            if (skipToken is not null) options["$skipToken"] = skipToken;

            var payload = JsonSerializer.Serialize(new { query = AccountsQuery, options });
            using var request = new HttpRequestMessage(HttpMethod.Post, ResourceGraphUri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            await AuthorizeAsync(request, ArmScope, ct);

            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in data.EnumerateArray())
                {
                    accounts.Add(new AccountInfo(
                        GetProp(row, "AccountId"),
                        GetProp(row, "AccountName"),
                        GetProp(row, "AccountEndpoint"),
                        GetProp(row, "SubscriptionId")));
                }
            }

            skipToken = root.TryGetProperty("$skipToken", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(skipToken));

        return accounts;
    }

    private async Task<List<JsonElement>> GetDeploymentsAsync(string accountId, CancellationToken ct)
    {
        string? uri = $"https://management.azure.com{accountId}/deployments?api-version=2024-10-01";
        var result = new List<JsonElement>();

        while (!string.IsNullOrEmpty(uri))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            await AuthorizeAsync(request, ArmScope, ct);

            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in value.EnumerateArray())
                    result.Add(d.Clone());
            }

            uri = doc.RootElement.TryGetProperty("nextLink", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        return result;
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, string scope, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static string NormalizeEndpoint(string endpoint) =>
        endpoint.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("/", "");

    private static string GetProp(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p)
            ? ElementToString(p)
            : "";

    private static string GetNested(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return "";
        }
        return ElementToString(current);
    }

    private static string ElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => element.GetRawText(),
    };

    private readonly record struct AccountInfo(
        string AccountId, string AccountName, string AccountEndpoint, string SubscriptionId);
}
