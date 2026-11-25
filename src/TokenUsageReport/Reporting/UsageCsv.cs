using System.Globalization;
using System.Text;

namespace TokenUsageReport.Reporting;

/// <summary>Flat, Power BI friendly export of a usage report at Day, Week (Monday start) or Month grain.</summary>
public static class UsageCsv
{
    private static readonly string[] Headers =
    {
        "TenantId", "Tenant", "SubscriptionId", "Subscription", "ResourceGroup", "FoundryResource", "FoundryProject", "AppInsights",
        "Agent", "AgentId", "Model", "Deployment", "PeriodStart", "PromptTokens", "CompletionTokens", "CachedTokens", "TotalTokens",
        "Calls", "Errors", "AvgLatencyMs", "ReportedCost", "EstimatedCostUsd", "CostBasis", "FirstSeen", "LastSeen",
        "ProjectPortalUrl", "AppInsightsPortalUrl",
    };

    public static string PeriodStart(string day, string granularity)
    {
        var d = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        d = granularity.ToLowerInvariant() switch
        {
            "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
            "month" => new DateOnly(d.Year, d.Month, 1),
            "day" => d,
            _ => throw new ArgumentException("granularity must be day, week or month."),
        };
        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static string Build(UsageReport report, string granularity)
    {
        var groups = report.Facts.GroupBy(f => (f.Scope, f.AgentName, f.AgentId, f.Model, f.Deployment, Period: PeriodStart(f.Day, granularity)))
            .OrderBy(g => g.Key.Period, StringComparer.Ordinal).ThenBy(g => g.Key.Scope).ThenBy(g => g.Key.AgentName, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers));
        foreach (var g in groups)
        {
            var s = report.Scopes[g.Key.Scope];
            var calls = g.Sum(f => f.Calls);
            var duration = g.Sum(f => f.DurationMsSum);
            var bases = g.Select(f => f.CostBasis).Distinct().ToList();
            var fields = new[]
            {
                Text(report.Tenant.Id), Text(report.Tenant.Name), Text(s.SubscriptionId), Text(s.SubscriptionName), Text(s.ResourceGroup),
                Text(s.AccountName), Text(s.ProjectName), Text(s.AppInsightsName), Text(g.Key.AgentName), Text(g.Key.AgentId),
                Text(g.Key.Model), Text(g.Key.Deployment), g.Key.Period,
                g.Sum(f => f.PromptTokens).ToString(CultureInfo.InvariantCulture), g.Sum(f => f.CompletionTokens).ToString(CultureInfo.InvariantCulture),
                g.Sum(f => f.CachedTokens).ToString(CultureInfo.InvariantCulture), g.Sum(f => f.TotalTokens).ToString(CultureInfo.InvariantCulture),
                calls.ToString(CultureInfo.InvariantCulture), g.Sum(f => f.Errors).ToString(CultureInfo.InvariantCulture),
                calls == 0 || duration == 0 ? "" : (duration / calls).ToString("0.#", CultureInfo.InvariantCulture),
                AllOrNothing(g.Select(f => f.ReportedCost)), AllOrNothing(g.Select(f => f.EstimatedCost)),
                bases.Count == 1 ? bases[0] : "mixed",
                g.Min(f => f.FirstSeen) ?? "", g.Max(f => f.LastSeen) ?? "", Text(s.PortalUrl), Text(s.AppInsightsPortalUrl),
            };
            sb.AppendLine(string.Join(",", fields));
        }
        return sb.ToString();
    }

    // A partial sum would understate cost, so a group with any unpriced day has no cost value.
    private static string AllOrNothing(IEnumerable<decimal?> values)
    {
        var list = values.ToList();
        return list.Count == 0 || list.Any(v => v is null) ? "" : list.Sum(v => v!.Value).ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>Quotes a value and defuses spreadsheet formulas, since agent and model names come from telemetry.</summary>
    internal static string Text(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
