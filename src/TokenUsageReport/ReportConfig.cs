namespace TokenUsageReport;

/// <summary>
/// Runtime configuration for the report, sourced from environment variables /
/// App Service application settings.
/// </summary>
public sealed class ReportConfig
{
    /// <summary>ARM resource id of the Application Insights component to query.</summary>
    public required string AppInsightsResourceId { get; init; }

    /// <summary>Blob service endpoint, e.g. https://mystorage.blob.core.windows.net/. Optional when writing to a local file.</summary>
    public string? BlobStorageEndpoint { get; init; }

    /// <summary>When set, the CSV is written to this local file path instead of Blob Storage (on-demand runs).</summary>
    public string? OutputFile { get; init; }

    /// <summary>Container that receives the CSV reports.</summary>
    public string Container { get; init; } = "token-reports";

    /// <summary>Size of the query window in hours (default: 24).</summary>
    public int LookbackHours { get; init; } = 24;

    /// <summary>
    /// Hours to subtract from today's UTC midnight to compute the end of the window.
    /// Allows time for late-arriving telemetry. Default 0 = end at UTC midnight (full previous day).
    /// </summary>
    public int EndOffsetHours { get; init; } = 0;

    /// <summary>Optional explicit report date (yyyy-MM-dd). When null, derived from the window start.</summary>
    public string? ReportDateOverride { get; init; }

    /// <summary>API key that protects the reports API. Callers must send it as the X-Api-Key header.</summary>
    public string? ApiKey { get; init; }

    /// <summary>UTC hour (0-23) at which the daily scheduled report runs. Default 6.</summary>
    public int ScheduleHourUtc { get; init; } = 6;

    /// <summary>Optional custom KQL (decoded from REPORT_QUERY_B64). When null the bundled default query is used.</summary>
    public string? ReportQuery { get; init; }

    public static ReportConfig FromEnvironment()
    {
        string Require(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(v))
                throw new InvalidOperationException($"Required setting '{name}' is not configured.");
            return v.Trim();
        }

        string? Optional(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        int OptionalInt(string name, int fallback)
        {
            var v = Optional(name);
            return v is not null && int.TryParse(v, out var parsed) ? parsed : fallback;
        }

        var outputFile = Optional("REPORT_OUTPUT_FILE");
        var endpoint = Optional("STORAGE_BLOB_ENDPOINT");
        if (endpoint is not null && !endpoint.EndsWith('/')) endpoint += "/";

        return new ReportConfig
        {
            AppInsightsResourceId = Require("APPINSIGHTS_RESOURCE_ID"),
            BlobStorageEndpoint = endpoint,
            OutputFile = outputFile,
            Container = Optional("REPORT_CONTAINER") ?? "token-reports",
            LookbackHours = OptionalInt("REPORT_LOOKBACK_HOURS", 24),
            EndOffsetHours = OptionalInt("REPORT_END_OFFSET_HOURS", 0),
            ReportDateOverride = Optional("REPORT_DATE"),
            ApiKey = Optional("REPORTS_API_KEY"),
            ScheduleHourUtc = OptionalInt("REPORT_SCHEDULE_HOUR_UTC", 6),
            ReportQuery = DecodeQuery(Optional("REPORT_QUERY_B64")),
        };
    }

    private static string? DecodeQuery(string? base64)
    {
        if (base64 is null) return null;
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
        catch (FormatException) { throw new InvalidOperationException("REPORT_QUERY_B64 is not valid base64."); }
    }

    /// <summary>Computes the (startUtc, endUtc) query window.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) ComputeWindow(
        int? lookbackHours = null,
        int? endOffsetHours = null,
        DateOnly? startDate = null,
        DateOnly? endDate = null)
    {
        if (startDate.HasValue != endDate.HasValue)
            throw new ArgumentException("Both startDate and endDate must be provided.");
        if (startDate is not null && endDate is not null)
        {
            if (startDate > endDate)
                throw new ArgumentException("startDate must be on or before endDate.");

            var explicitStart = new DateTimeOffset(startDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var explicitEnd = new DateTimeOffset(endDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return (explicitStart, explicitEnd);
        }

        var lookback = lookbackHours is > 0 ? lookbackHours.Value : LookbackHours;
        var endOffset = endOffsetHours ?? EndOffsetHours;
        var todayMidnightUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var end = todayMidnightUtc.AddHours(-endOffset);
        var start = end.AddHours(-lookback);
        return (start, end);
    }

    /// <summary>The label used in the blob path/name (yyyy-MM-dd).</summary>
    public string ResolveReportDate(
        int? lookbackHours = null,
        int? endOffsetHours = null,
        DateOnly? startDate = null,
        DateOnly? endDate = null)
    {
        if (!string.IsNullOrWhiteSpace(ReportDateOverride))
            return ReportDateOverride!;
        var (start, _) = ComputeWindow(lookbackHours, endOffsetHours, startDate, endDate);
        return start.UtcDateTime.ToString("yyyy-MM-dd");
    }
}
