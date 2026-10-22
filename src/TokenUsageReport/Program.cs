using Azure.Identity;
using TokenUsageReport;
using TokenUsageReport.Services;

var builder = WebApplication.CreateBuilder(args);

var config = ReportConfig.FromEnvironment();
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeInteractiveBrowserCredential = true,
});

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<Azure.Core.TokenCredential>(credential);
builder.Services.AddSingleton(sp => new TokenUsageReportService(config, credential));
builder.Services.AddSingleton(sp => new BlobReader(config.BlobStorageEndpoint, config.Container, credential));

// The daily scheduler only makes sense when a blob is configured to persist to.
// In the default (storage-less) mode the report is generated on demand via the API.
if (!string.IsNullOrWhiteSpace(config.BlobStorageEndpoint))
    builder.Services.AddHostedService<ScheduledReportService>();

var app = builder.Build();

// --- API key gate for /api/* -----------------------------------------------
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(config.ApiKey))
    {
        var provided = ctx.Request.Headers["X-Api-Key"].ToString();
        if (!string.Equals(provided, config.ApiKey, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid or missing X-Api-Key." });
            return;
        }
    }
    await next();
});

app.MapGet("/", () => Results.Text("Token Usage Report app is running. Reports API is under /api/ (X-Api-Key required)."));
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
            blobName = result.BlobName,
            csv = result.Csv,
        });
    }
    catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
}

// POST /api/run — default window (from app settings); persists to blob if configured.
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
});

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
