using Azure.Core;
using TokenUsageReport.Models;

namespace TokenUsageReport.Services;

/// <summary>The outcome of generating a report.</summary>
public sealed record ReportResult(string ReportDate, int RowCount, string Csv, string? BlobName, Uri? BlobUri);

/// <summary>
/// Orchestrates the token usage report:
/// 1. Query Application Insights for token usage in the window.
/// 2. Build the Cognitive Services inventory from Resource Graph + ARM.
/// 3. Left-join token rows with inventory (Backend + DeploymentName).
/// 4. Render CSV (and optionally upload to Blob Storage / write to a file).
/// </summary>
public sealed class TokenUsageReportService
{
    private readonly ReportConfig _config;
    private readonly AppInsightsClient _appInsights;
    private readonly InventoryClient _inventory;
    private readonly BlobWriter? _blob;

    public TokenUsageReportService(ReportConfig config, TokenCredential credential)
    {
        _config = config;
        _appInsights = new AppInsightsClient(config.AppInsightsResourceId, credential);
        _inventory = new InventoryClient(credential);
        _blob = string.IsNullOrWhiteSpace(config.BlobStorageEndpoint)
            ? null
            : new BlobWriter(config.BlobStorageEndpoint!, config.Container, credential);
    }

    /// <summary>Generates the report; uploads to blob when <paramref name="upload"/> is true and blob is configured.
    /// Optional lookback/offset or explicit start/end dates override the configured window.</summary>
    public async Task<ReportResult> GenerateAsync(
        bool upload,
        int? lookbackHours,
        int? endOffsetHours,
        CancellationToken ct,
        DateOnly? startDate = null,
        DateOnly? endDate = null)
    {
        var (start, end) = _config.ComputeWindow(lookbackHours, endOffsetHours, startDate, endDate);
        var reportDate = _config.ResolveReportDate(lookbackHours, endOffsetHours, startDate, endDate);
        Console.WriteLine($"Report date: {reportDate}  window: {start:u} -> {end:u} (UTC)");

        var tokenRows = await _appInsights.QueryAsync(start, end, ct);
        Console.WriteLine($"  {tokenRows.Count} token row(s) returned.");

        if (tokenRows.Count == 0)
            return new ReportResult(reportDate, 0, CsvWriter.Build(Enumerable.Empty<EnrichedRow>()), null, null);

        var inventory = await _inventory.BuildInventoryAsync(ct);
        Console.WriteLine($"  {inventory.Count} deployment(s) discovered.");

        var enriched = tokenRows.Select(t =>
        {
            inventory.TryGetValue(InventoryItem.MakeKey(t.Backend, t.DeploymentName), out var inv);
            return new EnrichedRow
            {
                SubscriptionId = t.SubscriptionId,
                DeploymentName = t.DeploymentName,
                Backend = t.Backend,
                ModelName = inv?.ModelName ?? "",
                AccountName = inv?.AccountName ?? "",
                CogSvcSubscriptionId = inv?.CogSvcSubscriptionId ?? "",
                AccountId = inv?.AccountId ?? "",
                SkuName = inv?.SkuName ?? "",
                SkuCapacity = inv?.SkuCapacity ?? "",
                PromptTokens = t.PromptTokens,
                CompletionTokens = t.CompletionTokens,
                CachedTokens = t.CachedTokens,
                TotalTokens = t.TotalTokens,
                Calls = t.Calls,
            };
        }).ToList();

        var csv = CsvWriter.Build(enriched);
        var blobName = $"{reportDate}/token-usage-daily-{reportDate}.csv";

        Uri? blobUri = null;
        if (upload && _blob is not null)
        {
            Console.WriteLine($"Uploading report to container '{_config.Container}' as '{blobName}'...");
            blobUri = await _blob.UploadAsync(blobName, csv, ct);
            Console.WriteLine($"Report written: {blobUri}");
        }

        if (!string.IsNullOrWhiteSpace(_config.OutputFile))
        {
            await File.WriteAllTextAsync(_config.OutputFile!, csv, ct);
            Console.WriteLine($"Report written to file: {_config.OutputFile}");
        }

        return new ReportResult(reportDate, enriched.Count, csv, blobName, blobUri);
    }
}
