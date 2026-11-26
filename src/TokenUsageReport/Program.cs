using Azure.Identity;
using TokenUsageReport;
using TokenUsageReport.Reporting;
using TokenUsageReport.Services;

var builder = WebApplication.CreateBuilder(args);

var config = ReportConfig.FromEnvironment();
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeInteractiveBrowserCredential = true,
});

// Two modes: the Foundry hierarchy report (REPORT_SUBSCRIPTIONS) and the legacy single Application Insights report.
var hierarchy = config.Subscriptions.Count > 0;
var legacy = !string.IsNullOrWhiteSpace(config.AppInsightsResourceId);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<Azure.Core.TokenCredential>(credential);
builder.Services.AddSingleton(sp => new BlobReader(config.BlobStorageEndpoint, config.Container, credential));
if (legacy)
    builder.Services.AddSingleton(sp => new TokenUsageReportService(config, credential));
if (hierarchy)
{
    builder.Services.AddSingleton(sp => new UsageReportService(
        new FoundryDiscovery(new ArmClient(credential)), new UsageQueryRunner(credential), new PriceBook(overrides: config.PriceOverrides)));
}

// The daily scheduler only makes sense when a blob is configured to persist to.
// In the default (storage-less) mode the report is generated on demand via the API.
if (legacy && !string.IsNullOrWhiteSpace(config.BlobStorageEndpoint))
    builder.Services.AddHostedService<ScheduledReportService>();

var app = builder.Build();

// --- API key gate for /api/* -----------------------------------------------
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
    {
        // Fail closed: never serve token data if no key has been configured.
        if (string.IsNullOrEmpty(config.ApiKey))
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { error = "REPORTS_API_KEY is not configured on the app." });
            return;
        }
        var provided = System.Text.Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Api-Key"].ToString());
        var expected = System.Text.Encoding.UTF8.GetBytes(config.ApiKey);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid or missing X-Api-Key." });
            return;
        }
    }
    await next();
});

// The dashboard page and its scripts are public (they contain no data); the data endpoints under /api/ need the API key.
foreach (var asset in WebAssets.Names)
{
    var body = WebAssets.ReadText(asset);
    var contentType = WebAssets.ContentType(asset);
    var route = asset == "index.html" ? "/" : "/" + asset;
    app.MapGet(route, (HttpContext ctx) =>
    {
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        return Results.Text(body, contentType);
    });
}
app.MapGet("/healthz", () => Results.Json(new { ok = true, blob = app.Services.GetRequiredService<BlobReader>().Enabled }));

// Generate a report on demand and return the CSV inline.
// Optional query params let callers (portal / Power BI) request any window:
//   ?lookbackHours=168&endOffsetHours=0  -> last 7 days
static async Task<IResult> RunReport(
    TokenUsageReportService svc,
    int? lookbackHours,
    int? endOffsetHours,
    DateOnly? startDate,
    DateOnly? endDate,
    bool upload,
    CancellationToken ct)
{
    try
    {
        var result = await svc.GenerateAsync(upload, lookbackHours, endOffsetHours, ct, startDate, endDate);
        return Results.Json(new
        {
            reportDate = result.ReportDate,
            rowCount = result.RowCount,
            empty = result.RowCount == 0,
            querySource = result.QuerySource,
            warning = result.Warning,
            blobName = result.BlobName,
            windowStartUtc = result.WindowStart,
            windowEndUtc = result.WindowEnd,
            csv = result.Csv,
        });
    }
    catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
}

// POST /api/run — default window (from app settings); persists to blob if configured.
if (legacy)
{
    app.MapPost("/api/run", (TokenUsageReportService svc, int? lookbackHours, int? endOffsetHours, DateOnly? startDate, DateOnly? endDate, CancellationToken ct) =>
        RunReport(svc, lookbackHours, endOffsetHours, startDate, endDate, upload: true, ct));

    // GET /api/report — flexible read-only generation (no blob write).
    app.MapGet("/api/report", (TokenUsageReportService svc, int? lookbackHours, int? endOffsetHours, DateOnly? startDate, DateOnly? endDate, CancellationToken ct) =>
        RunReport(svc, lookbackHours, endOffsetHours, startDate, endDate, upload: false, ct));

    // GET /api/report.csv — raw text/csv (Power BI Web connector friendly).
    app.MapGet("/api/report.csv", async (TokenUsageReportService svc, int? lookbackHours, int? endOffsetHours, DateOnly? startDate, DateOnly? endDate, CancellationToken ct) =>
    {
        try
        {
            var result = await svc.GenerateAsync(upload: false, lookbackHours, endOffsetHours, ct, startDate, endDate);
            return Results.Text(result.Csv, "text/csv");
        }
        catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
    });
}

// Foundry hierarchy report: tenant > subscriptions > resource groups > Foundry resources (+ linked Application Insights)
// > agents / models. The scope is the REPORT_SUBSCRIPTIONS setting; discovery runs with the app's managed identity.
if (hierarchy)
{
    async Task<IResult> BuildUsage(UsageReportService svc, int? lookbackHours, int? endOffsetHours, DateOnly? startDate, DateOnly? endDate, string format, string granularity, CancellationToken ct)
    {
        try
        {
            var (start, end) = config.ComputeWindow(lookbackHours, endOffsetHours, startDate, endDate);
            var report = await svc.RunAsync(new ReportRequest(config.Subscriptions, start, end, config.TenantId, config.TenantName), ct);
            return format == "csv" ? Results.Text(UsageCsv.Build(report, granularity), "text/csv") : Results.Json(report);
        }
        catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
    }

    // GET /api/usage?startDate=2026-11-01&endDate=2026-11-24 — full hierarchical report as JSON.
    app.MapGet("/api/usage", (UsageReportService svc, int? lookbackHours, int? endOffsetHours, DateOnly? startDate, DateOnly? endDate, CancellationToken ct) =>
        BuildUsage(svc, lookbackHours, endOffsetHours, startDate, endDate, "json", "day", ct));

    // GET /api/usage.csv?granularity=day|week|month — flat detail rows for Power BI.
    app.MapGet("/api/usage.csv", (UsageReportService svc, int? lookbackHours, int? endOffsetHours, DateOnly? startDate, DateOnly? endDate, string? granularity, CancellationToken ct) =>
        BuildUsage(svc, lookbackHours, endOffsetHours, startDate, endDate, "csv", granularity ?? "day", ct));
}

// Optional blob-backed history (only meaningful when a storage endpoint is configured).
app.MapGet("/api/reports", async (BlobReader reader, CancellationToken ct) =>
{
    try
    {
        var blobs = await reader.ListAsync(ct);
        return Results.Json(new { reports = blobs.Select(b => new { name = b.Name, size = b.Size, modified = b.Modified }) });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapGet("/api/reports/content", async (string name, BlobReader reader, CancellationToken ct) =>
{
    try
    {
        var csv = await reader.DownloadTextAsync(name, ct);
        if (csv is null) return Results.Json(new { error = "Report not found." }, statusCode: 404);
        return Results.Json(new { name, csv });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.Run();
