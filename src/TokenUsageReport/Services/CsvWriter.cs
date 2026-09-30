using System.Globalization;
using System.Text;
using TokenUsageReport.Models;

namespace TokenUsageReport.Services;

/// <summary>Serializes enriched report rows to CSV (RFC 4180 quoting).</summary>
public static class CsvWriter
{
    private static readonly string[] Headers =
    {
        "SubscriptionId", "DeploymentName", "Backend", "ModelName", "AccountName",
        "CogSvcSubscriptionId", "AccountId", "SkuName", "SkuCapacity",
        "PromptTokens", "CompletionTokens", "CachedTokens", "TotalTokens", "Calls",
    };

    public static string Build(IEnumerable<EnrichedRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers.Select(Escape)));

        foreach (var r in rows)
        {
            var fields = new[]
            {
                r.SubscriptionId,
                r.DeploymentName,
                r.Backend,
                r.ModelName,
                r.AccountName,
                r.CogSvcSubscriptionId,
                r.AccountId,
                r.SkuName,
                r.SkuCapacity,
                r.PromptTokens.ToString(CultureInfo.InvariantCulture),
                r.CompletionTokens.ToString(CultureInfo.InvariantCulture),
                r.CachedTokens.ToString(CultureInfo.InvariantCulture),
                r.TotalTokens.ToString(CultureInfo.InvariantCulture),
                r.Calls.ToString(CultureInfo.InvariantCulture),
            };
            sb.AppendLine(string.Join(",", fields.Select(Escape)));
        }

        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        value ??= "";
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
