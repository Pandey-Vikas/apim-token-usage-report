namespace TokenUsageReport.Services;

/// <summary>
/// Runs the token usage report once per day at the configured UTC hour.
/// Replaces the old triggered WebJob — the app hosts its own schedule.
/// </summary>
public sealed class ScheduledReportService : BackgroundService
{
    private readonly TokenUsageReportService _report;
    private readonly ReportConfig _config;
    private readonly ILogger<ScheduledReportService> _logger;

    public ScheduledReportService(
        TokenUsageReportService report, ReportConfig config, ILogger<ScheduledReportService> logger)
    {
        _report = report;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextRun();
            _logger.LogInformation("Next scheduled report run in {Delay} (at {Hour:00}:00 UTC).", delay, _config.ScheduleHourUtc);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                var y = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
                // The stored daily report is always the previous full UTC day.
                var result = await _report.GenerateAsync(upload: true, null, null, stoppingToken, y, y);
                _logger.LogInformation("Scheduled report {Date}: {Rows} row(s).", result.ReportDate, result.RowCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled report run failed.");
            }
        }
    }

    private TimeSpan TimeUntilNextRun()
    {
        var now = DateTime.UtcNow;
        var hour = Math.Clamp(_config.ScheduleHourUtc, 0, 23);
        var next = new DateTime(now.Year, now.Month, now.Day, hour, 0, 0, DateTimeKind.Utc);
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }
}
