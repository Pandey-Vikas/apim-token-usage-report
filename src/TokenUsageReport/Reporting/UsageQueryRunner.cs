using System.Globalization;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace TokenUsageReport.Reporting;

public interface IUsageSource
{
    Task<List<UsageRow>> QueryAsync(string appInsightsId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct);
}

/// <summary>Runs the bundled foundry-usage.kql against one Application Insights component.</summary>
public sealed class UsageQueryRunner : IUsageSource
{
    public static readonly string[] RequiredColumns =
    {
        "Day", "ProjectId", "AgentId", "AgentName", "Model", "Deployment", "Backend", "PromptTokens", "CompletionTokens",
        "CachedTokens", "Calls", "Errors", "DurationMsSum", "ReportedCost", "CostCurrency", "FirstSeen", "LastSeen",
    };

    private readonly LogsQueryClient _client;
    private readonly string _query;

    public UsageQueryRunner(LogsQueryClient client, string? query = null)
    {
        _client = client;
        _query = query ?? LoadBundledQuery();
    }

    public UsageQueryRunner(TokenCredential credential) : this(new LogsQueryClient(credential)) { }

    public static string LoadBundledQuery()
    {
        using var stream = typeof(UsageQueryRunner).Assembly.GetManifestResourceStream("foundry-usage.kql")
            ?? throw new InvalidOperationException("The bundled foundry-usage.kql resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task<List<UsageRow>> QueryAsync(string appInsightsId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var response = await _client.QueryResourceAsync(new ResourceIdentifier(appInsightsId), _query, new QueryTimeRange(start, end), cancellationToken: ct);
        if (response.Value.Status != LogsQueryResultStatus.Success)
            throw new InvalidOperationException($"The query did not complete: {response.Value.Error?.Message}");
        return ReadRows(response.Value.Table);
    }

    public static List<UsageRow> ReadRows(LogsTable table)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Columns.Count; i++) index[table.Columns[i].Name] = i;
        var missing = RequiredColumns.Where(c => !index.ContainsKey(c)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"The usage query is missing columns: {string.Join(", ", missing)}.");

        var rows = new List<UsageRow>();
        foreach (var row in table.Rows)
        {
            object? V(string name) => row[index[name]];
            rows.Add(new UsageRow(
                Day: Date(V("Day"))?.UtcDateTime.Date ?? throw new InvalidOperationException("A usage row has no Day."),
                ProjectId: Text(V("ProjectId")), AgentId: Text(V("AgentId")), AgentName: Text(V("AgentName")),
                Model: Text(V("Model")), Deployment: Text(V("Deployment")), Backend: Text(V("Backend")),
                PromptTokens: Long(V("PromptTokens")), CompletionTokens: Long(V("CompletionTokens")), CachedTokens: Long(V("CachedTokens")),
                Calls: Long(V("Calls")), Errors: Long(V("Errors")), DurationMsSum: Double(V("DurationMsSum")),
                ReportedCost: Decimal(V("ReportedCost")), CostCurrency: Text(V("CostCurrency")),
                FirstSeen: Date(V("FirstSeen")), LastSeen: Date(V("LastSeen"))));
        }
        return rows;
    }

    private static string Text(object? v) => v?.ToString() ?? "";

    private static long Long(object? v) => v switch
    {
        null => 0, long l => l, int i => i, double d => (long)d, decimal m => (long)m,
        _ => long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : throw new InvalidOperationException($"Invalid number '{v}'."),
    };

    private static double Double(object? v) => v switch
    {
        null => 0, double d => d, long l => l, int i => i, decimal m => (double)m,
        _ => double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0,
    };

    private static decimal? Decimal(object? v) => v switch
    {
        null => null, decimal m => m, double d => (decimal)d, long l => l,
        _ => decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
    };

    private static DateTimeOffset? Date(object? v) => v switch
    {
        null => null, DateTimeOffset o => o, DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        _ => DateTimeOffset.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null,
    };
}
