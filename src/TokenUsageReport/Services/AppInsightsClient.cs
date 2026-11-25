using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using System.Globalization;
using TokenUsageReport.Models;

namespace TokenUsageReport.Services;

public sealed record TokenQueryResult(List<TokenUsageRow> Rows, string Source, string? Warning);

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
    private readonly bool _automatic;
    public static readonly string[] RequiredColumns =
        { "SubscriptionId", "DeploymentName", "Backend", "PromptTokens", "CompletionTokens", "CachedTokens", "TotalTokens", "Calls" };

    public AppInsightsClient(string appInsightsResourceId, TokenCredential credential, string? customQuery = null)
        : this(appInsightsResourceId, new LogsQueryClient(credential), customQuery)
    {
    }

    public AppInsightsClient(string appInsightsResourceId, LogsQueryClient client, string? customQuery = null)
    {
        _client = client;
        _resourceId = new ResourceIdentifier(appInsightsResourceId);
        _automatic = string.IsNullOrWhiteSpace(customQuery);
        _query = string.IsNullOrWhiteSpace(customQuery) ? LoadDefaultQuery() : customQuery;
    }

    public static string LoadDefaultQuery() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Queries", DefaultQueryFile));

    public async Task<TokenQueryResult> QueryAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var queries = _automatic
            ? new[] { ("apim-token-metrics", _query), ("foundry-genai", LoadQuery("foundry-genai")), ("genai-token-metric", LoadQuery("genai-token-metric")) }
            : new[] { ("custom", _query) };
        foreach (var (source, query) in queries)
        {
            var rows = await ExecuteAsync(query, start, end, ct);
            Console.WriteLine($"  Query source '{source}': {rows.Count} usage row(s).");
            if (rows.Count == 0) continue;
            var warnings = new List<string>();
            if (source != "apim-token-metrics" && rows.Any(r => string.IsNullOrEmpty(r.AgentId) && string.IsNullOrEmpty(r.AgentName)))
                warnings.Add("Some usage has no attributable agent identity. These rows are retained with blank agent fields; the source must emit agent attributes and correlated spans to identify them.");
            if (source != "apim-token-metrics" && rows.Any(r => r.ReportedCost is null))
                warnings.Add("ReportedCost is blank where complete cost telemetry was not emitted. Foundry portal cost estimates are not automatically stored in traces; no price or currency is assumed.");
            if (rows.Any(r => r.ReportedCost is not null && string.IsNullOrEmpty(r.CostCurrency)))
                warnings.Add("Some reported costs have no emitted currency. Do not combine costs across unknown currencies.");
            return new TokenQueryResult(rows, source, warnings.Count == 0 ? null : string.Join(" ", warnings));
        }
        return new TokenQueryResult(new(), _automatic ? "automatic" : "custom",
            $"No supported token usage was returned from {_resourceId} in {start:u} to {end:u}. " +
            "Check the selected Application Insights, date range and ingestion delay. Traces without token attributes cannot produce token totals. Use Diagnose to inspect the available fields.");
    }

    private static string LoadQuery(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Queries", name + ".kql"));

    private async Task<List<TokenUsageRow>> ExecuteAsync(
        string query, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var response = await _client.QueryResourceAsync(
            _resourceId,
            query,
            new QueryTimeRange(start, end),
            cancellationToken: ct);

        if (response.Value.Status != LogsQueryResultStatus.Success)
            throw new InvalidOperationException($"Application Insights query did not complete: {response.Value.Error?.Message}");
        return ReadRows(response.Value.Table);
    }

    public static List<TokenUsageRow> ReadRows(LogsTable table)
    {
        var rows = new List<TokenUsageRow>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Columns.Count; i++) index[table.Columns[i].Name] = i;
        var missing = RequiredColumns.Where(c => !index.ContainsKey(c)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Report query is missing required columns: {string.Join(", ", missing)}.");

        foreach (var row in table.Rows)
        {
            rows.Add(new TokenUsageRow
            {
                SubscriptionId = GetString(row, index, "SubscriptionId"),
                DeploymentName = GetString(row, index, "DeploymentName"),
                Backend = GetString(row, index, "Backend"),
                AgentId = GetString(row, index, "AgentId"),
                AgentName = GetString(row, index, "AgentName"),
                ReportedCost = GetDecimal(row, index, "ReportedCost"),
                CostCurrency = GetString(row, index, "CostCurrency"),
                ResourceId = GetString(row, index, "ResourceId"),
                PromptTokens = GetLong(row, index, "PromptTokens"),
                CompletionTokens = GetLong(row, index, "CompletionTokens"),
                CachedTokens = GetLong(row, index, "CachedTokens"),
                TotalTokens = GetLong(row, index, "TotalTokens"),
                Calls = GetLong(row, index, "Calls"),
            });
        }
        return rows;
    }

    // Agent/cost columns are optional for APIM and existing custom queries.
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
            _ => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed : throw new InvalidOperationException($"Report column '{column}' contains an invalid token count."),
        };
    }

    private static decimal? GetDecimal(LogsTableRow row, Dictionary<string, int> index, string column)
    {
        var value = Cell(row, index, column);
        if (value is null || string.IsNullOrWhiteSpace(value.ToString())) return null;
        return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new InvalidOperationException($"Report column '{column}' contains an invalid cost.");
    }
}
