using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using TokenUsageReport.Models;

namespace TokenUsageReport.Services;

/// <summary>
/// Queries Application Insights <c>customMetrics</c> for LLM token usage using the
/// Azure Monitor Query data plane (authenticated with the app's managed identity).
/// </summary>
public sealed class AppInsightsClient
{
    private const string Query = """
        let calls = customMetrics
        | where name == 'Total Tokens'
        | extend DeploymentName = tostring(customDimensions['Deployment']),
                 SubscriptionId = tostring(customDimensions['Subscription ID']),
                 Backend = tostring(customDimensions['Backend'])
        | summarize Calls = sum(valueCount) by SubscriptionId, DeploymentName, Backend;
        let tokenData = customMetrics
        | where name in ('Prompt Tokens', 'Completion Tokens', 'Prompt Cached Tokens')
        | extend DeploymentName = tostring(customDimensions['Deployment']),
                 SubscriptionId = tostring(customDimensions['Subscription ID']),
                 Backend = tostring(customDimensions['Backend'])
        | summarize TokenCount = sum(valueSum) by SubscriptionId, DeploymentName, Backend, name
        | evaluate pivot(name, sum(TokenCount))
        | extend PromptTokens = tolong(coalesce(column_ifexists('Prompt Tokens', 0.0), 0.0)),
                 CompletionTokens = tolong(coalesce(column_ifexists('Completion Tokens', 0.0), 0.0)),
                 CachedTokens = tolong(coalesce(column_ifexists('Prompt Cached Tokens', 0.0), 0.0))
        | extend TotalTokens = PromptTokens + CompletionTokens
        | project SubscriptionId, DeploymentName, Backend, PromptTokens, CompletionTokens, CachedTokens, TotalTokens;
        tokenData
        | join kind=inner calls on SubscriptionId, DeploymentName, Backend
        | project SubscriptionId, DeploymentName, Backend, PromptTokens, CompletionTokens, CachedTokens, TotalTokens, Calls
        | order by SubscriptionId asc, TotalTokens desc
        """;

    private readonly LogsQueryClient _client;
    private readonly ResourceIdentifier _resourceId;

    public AppInsightsClient(string appInsightsResourceId, TokenCredential credential)
    {
        _client = new LogsQueryClient(credential);
        _resourceId = new ResourceIdentifier(appInsightsResourceId);
    }

    public async Task<List<TokenUsageRow>> QueryAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var response = await _client.QueryResourceAsync(
            _resourceId,
            Query,
            new QueryTimeRange(start, end),
            cancellationToken: ct);

        var rows = new List<TokenUsageRow>();
        var table = response.Value.Table;
        foreach (var row in table.Rows)
        {
            rows.Add(new TokenUsageRow
            {
                SubscriptionId = GetString(row, "SubscriptionId"),
                DeploymentName = GetString(row, "DeploymentName"),
                Backend = GetString(row, "Backend"),
                PromptTokens = GetLong(row, "PromptTokens"),
                CompletionTokens = GetLong(row, "CompletionTokens"),
                CachedTokens = GetLong(row, "CachedTokens"),
                TotalTokens = GetLong(row, "TotalTokens"),
                Calls = GetLong(row, "Calls"),
            });
        }
        return rows;
    }

    private static string GetString(LogsTableRow row, string column)
    {
        var value = row[column];
        return value?.ToString() ?? "";
    }

    private static long GetLong(LogsTableRow row, string column)
    {
        var value = row[column];
        return value switch
        {
            null => 0,
            long l => l,
            int i => i,
            double d => (long)d,
            decimal m => (long)m,
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : 0,
        };
    }
}
