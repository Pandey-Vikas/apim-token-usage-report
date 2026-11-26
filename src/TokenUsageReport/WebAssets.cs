namespace TokenUsageReport;

/// <summary>
/// The report dashboard (the same HTML/JS/CSS the local portal uses), embedded in the app and served from memory.
/// The page itself carries no data: every figure comes from /api/usage, which requires the X-Api-Key header.
/// </summary>
public static class WebAssets
{
    // The portal's index.html contains this marker; the app swaps it for a flag that switches the page to hosted mode.
    public const string HostedMarker = "<!--HOSTED-->";
    public const string HostedScript = "<script>window.HOSTED = true;</script>";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["index.html"] = "text/html; charset=utf-8",
        ["styles.css"] = "text/css; charset=utf-8",
        ["wizard.js"] = "text/javascript; charset=utf-8",
        ["dashboard.js"] = "text/javascript; charset=utf-8",
    };

    public static IEnumerable<string> Names => ContentTypes.Keys;

    public static string ContentType(string name) => ContentTypes[name];

    public static string ReadText(string name)
    {
        using var stream = typeof(WebAssets).Assembly.GetManifestResourceStream($"web/{name}")
            ?? throw new InvalidOperationException($"The embedded web asset '{name}' is missing.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        if (!name.Equals("index.html", StringComparison.OrdinalIgnoreCase)) return text;
        if (!text.Contains(HostedMarker, StringComparison.Ordinal))
            throw new InvalidOperationException($"index.html has no {HostedMarker} marker, so hosted mode cannot be enabled.");
        return text.Replace(HostedMarker, HostedScript, StringComparison.Ordinal);
    }
}
