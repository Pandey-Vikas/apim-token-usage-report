using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using TokenUsageReport.Models;

namespace TokenUsageReport.Services;

/// <summary>
/// Runs the token-usage KQL query against Application Insights using the Azure Monitor Query data plane
/// (authenticated with the app's managed identity). The query is either the bundled default
/// (Queries/apim-token-metrics.kql) or a custom one supplied at deploy time.
/// </summary>
public sealed class AppInsightsClient
{
    public const string DefaultQueryFile = "apim-token-metrics.kql";

    private readonly LogsQueryClient _client;
    private readonly ResourceIdentifier _resourceId;
    private readonly string _query;

    public AppInsightsClient(string appInsightsResourceId, TokenCredential credential, string? customQuery = null)
    {
        _client = new LogsQueryClient(credential);
        _resourceId = new ResourceIdentifier(appInsightsResourceId);
        _query = string.IsNullOrWhiteSpace(customQuery) ? LoadDefaultQuery() : customQuery;
    }

    public static string LoadDefaultQuery() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Queries", DefaultQueryFile));

    public async Task<List<TokenUsageRow>> QueryAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var response = await _client.QueryResourceAsync(
            _resourceId,
            _query,
            new QueryTimeRange(start, end),
            cancellationToken: ct);

        var rows = new List<TokenUsageRow>();
        var table = response.Value.Table;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Columns.Count; i++) index[table.Columns[i].Name] = i;

        foreach (var row in table.Rows)
        {
            rows.Add(new TokenUsageRow
            {
                SubscriptionId = GetString(row, index, "SubscriptionId"),
                DeploymentName = GetString(row, index, "DeploymentName"),
                Backend = GetString(row, index, "Backend"),
                PromptTokens = GetLong(row, index, "PromptTokens"),
                CompletionTokens = GetLong(row, index, "CompletionTokens"),
                CachedTokens = GetLong(row, index, "CachedTokens"),
                TotalTokens = GetLong(row, index, "TotalTokens"),
                Calls = GetLong(row, index, "Calls"),
            });
        }
        return rows;
    }

    // Custom queries may omit optional columns; a missing column reads as empty/0 instead of failing the report.
    private static object? Cell(LogsTableRow row, Dictionary<string, int> index, string column) =>
        index.TryGetValue(column, out var i) ? row[i] : null;

    private static string GetString(LogsTableRow row, Dictionary<string, int> index, string column) =>
        Cell(row, index, column)?.ToString() ?? "";

    private static long GetLong(LogsTableRow row, Dictionary<string, int> index, string column)
    {
        var value = Cell(row, index, column);
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
