using System.Text;
using System.Text.Json;
using Azure.Identity;
using DeployPortal.Services;
using TokenUsageReport.Reporting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<AzCli>();
builder.Services.AddSingleton<LoginManager>();
builder.Services.AddSingleton<Deployer>();
builder.Services.AddHttpClient();

var app = builder.Build();

// The portal controls your Azure login and deployments, so only allow genuine local browser
// access: reject foreign Host headers (DNS rebinding) and cross-site state-changing requests (CSRF).
app.Use(async (ctx, next) =>
{
    static bool IsLocalHost(string? host) =>
        host is not null && (host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                             host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                             host.Equals("[::1]", StringComparison.Ordinal));

    var origin = ctx.Request.Headers.Origin.ToString();
    var originOk = string.IsNullOrEmpty(origin) ||
                   (Uri.TryCreate(origin, UriKind.Absolute, out var o) && IsLocalHost(o.IsLoopback ? o.Host : null));
    if (!IsLocalHost(ctx.Request.Host.Host) || !originOk)
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsync("Forbidden: portal is local-only.");
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Local dev portal — always revalidate so UI edits show up immediately.
        ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    },
});

var az = app.Services.GetRequiredService<AzCli>();
var login = app.Services.GetRequiredService<LoginManager>();
var deployer = app.Services.GetRequiredService<Deployer>();
var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();

// Persisted deployment config (so the portal remembers what was deployed).
var configPath = Path.Combine(app.Environment.ContentRootPath, "deploy-config.json");

// --- Auth / status ---------------------------------------------------------
app.MapGet("/api/status", async () =>
{
    try
    {
        var account = await az.JsonAsync(new[] { "account", "show" });
        return Results.Json(new { signedIn = account is not null, account });
    }
    catch
    {
        return Results.Json(new { signedIn = false, account = (object?)null });
    }
});

app.MapPost("/api/login-start", async (LoginBody body) =>
{
    await login.StartBrowserLoginAsync(body?.TenantId);
    return Results.Json(new { ok = true });
});

app.MapPost("/api/login-device-code", async (LoginBody body) =>
{
    await login.StartDeviceCodeLoginAsync(body?.TenantId);
    return Results.Json(new { ok = true });
});

app.MapGet("/api/login-poll", async () =>
{
    var s = login.Snapshot();
    if (s.Phase == "idle")
        return Results.Json(new { state = "idle" });

    JsonElement? account = null;
    try { account = await az.JsonAsync(new[] { "account", "show" }, timeoutMs: 15_000); }
    catch { /* no valid session yet */ }

    if (account is not null)
    {
        var currentKey = LoginManager.AccountKey(account);
        var currentTenant = account.Value.TryGetProperty("tenantId", out var t) ? t.GetString() : null;
        // Only a GUID can be compared to account.tenantId; a domain name (contoso.onmicrosoft.com) can't.
        var tenantOk = string.IsNullOrWhiteSpace(s.RequestedTenant)
            || !Guid.TryParse(s.RequestedTenant, out _)
            || string.Equals(currentTenant, s.RequestedTenant, StringComparison.OrdinalIgnoreCase);

        bool done;
        if (s.Phase is "done")
        {
            done = true;                                   // already confirmed
        }
        else if (s.Phase == "exited")
        {
            // The login process finished — `az account show` now reflects the chosen account.
            done = tenantOk;
        }
        else // running: resolve when the account changed OR the token cache was just written
        {
            var changed = s.BaselineKey is null
                || !string.Equals(currentKey, s.BaselineKey, StringComparison.Ordinal);
            done = tenantOk && (changed || login.CacheAdvanced());
        }

        if (done)
        {
            login.MarkResolved();
            return Results.Json(new { state = "success", account });
        }
    }

    if (s.Phase == "exited" && account is null)
        return Results.Json(new { state = "failed", error = s.Error ?? "Sign-in did not complete." });

    return Results.Json(new { state = "pending", url = s.Url, code = s.Code });
});

app.MapPost("/api/login-cancel", () => { login.Cancel(); return Results.Json(new { ok = true }); });

// --- Subscriptions / resource groups / locations ---------------------------
app.MapGet("/api/subscriptions", async (string? tenantId) =>
{
    try
    {
        var subs = await az.JsonAsync(new[] { "account", "list" });
        if (subs is null || subs.Value.ValueKind != JsonValueKind.Array)
            return Results.Json(Array.Empty<object>());
        // Only show subscriptions from the selected tenant (the CLI cache can hold several tenants).
        var filtered = subs.Value.EnumerateArray().Where(s =>
            (string.IsNullOrWhiteSpace(tenantId) ||
             (s.TryGetProperty("tenantId", out var t) &&
              string.Equals(t.GetString(), tenantId, StringComparison.OrdinalIgnoreCase)))
            && !(s.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
                 && !string.Equals(st.GetString(), "Enabled", StringComparison.OrdinalIgnoreCase))
            && !(s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                 && (n.GetString() ?? "").StartsWith("N/A(tenant level account)", StringComparison.OrdinalIgnoreCase)));
        return Results.Json(filtered.ToList());
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// Tenants (directories) the signed-in user can pick from: ARM tenant list + tenants seen in the CLI cache.
app.MapGet("/api/tenants", async () =>
{
    var tenants = new Dictionary<string, (string Name, string Domain)>(StringComparer.OrdinalIgnoreCase);
    string? current = null;
    try
    {
        var acct = await az.JsonAsync(new[] { "account", "show" }, timeoutMs: 15_000);
        if (acct is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("tenantId", out var ct))
            current = ct.GetString();
    }
    catch { /* not signed in */ }

    try
    {
        var arm = await az.JsonAsync(new[] { "rest", "--method", "get",
            "--url", "https://management.azure.com/tenants?api-version=2022-12-01" }, timeoutMs: 30_000);
        if (arm is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var t in v.EnumerateArray())
            {
                var id = t.TryGetProperty("tenantId", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                tenants[id] = (
                    t.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                    t.TryGetProperty("defaultDomain", out var dd) ? dd.GetString() ?? "" : "");
            }
    }
    catch { /* fall back to the cached accounts below */ }

    try
    {
        var subs = await az.JsonAsync(new[] { "account", "list" });
        if (subs is { ValueKind: JsonValueKind.Array })
            foreach (var s in subs.Value.EnumerateArray())
            {
                var id = s.TryGetProperty("tenantId", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(id) || tenants.ContainsKey(id)) continue;
                tenants[id] = (
                    s.TryGetProperty("tenantDisplayName", out var dn) ? dn.GetString() ?? "" : "",
                    s.TryGetProperty("tenantDefaultDomain", out var dd) ? dd.GetString() ?? "" : "");
            }
    }
    catch { /* ignore */ }

    if (current is not null && !tenants.ContainsKey(current)) tenants[current] = ("", "");

    var list = tenants
        .Select(kv => new { tenantId = kv.Key, displayName = kv.Value.Name, defaultDomain = kv.Value.Domain, current = string.Equals(kv.Key, current, StringComparison.OrdinalIgnoreCase) })
        .OrderByDescending(x => x.current).ThenBy(x => x.displayName, StringComparer.OrdinalIgnoreCase);
    return Results.Json(new { currentTenantId = current, tenants = list });
});

app.MapPost("/api/subscription", async (SubscriptionBody body) =>
{
    if (string.IsNullOrWhiteSpace(body?.SubscriptionId))
        return Results.Json(new { error = "subscriptionId is required" }, statusCode: 400);
    try
    {
        await az.JsonAsync(new[] { "account", "set", "--subscription", body.SubscriptionId });
        return Results.Json(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapGet("/api/resource-groups", async () =>
{
    try
    {
        var groups = await az.JsonAsync(new[] { "group", "list" });
        return Results.Json(groups ?? JsonDocument.Parse("[]").RootElement);
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapGet("/api/locations", async () =>
{
    string[] preferred =
    {
        "eastus", "eastus2", "westus", "westus2", "westus3", "centralus",
        "southcentralus", "northcentralus", "westeurope", "northeurope",
        "swedencentral", "uksouth", "francecentral", "centralindia", "southindia", "westindia", "australiaeast", "japaneast", "canadacentral",
    };
    try
    {
        var locs = await az.JsonAsync(new[] { "account", "list-locations" });
        if (locs is null || locs.Value.ValueKind != JsonValueKind.Array)
            return Results.Json(preferred.Select(n => new { name = n, displayName = n, preferred = true }));

        var set = preferred.ToHashSet();
        var rich = locs.Value.EnumerateArray()
            .Where(l => l.TryGetProperty("metadata", out var m)
                        && m.TryGetProperty("regionType", out var rt)
                        && string.Equals(rt.GetString(), "Physical", StringComparison.OrdinalIgnoreCase))
            .Select(l => new
            {
                name = l.GetProperty("name").GetString() ?? "",
                displayName = l.TryGetProperty("displayName", out var d) ? d.GetString() ?? "" : "",
                preferred = set.Contains(l.GetProperty("name").GetString() ?? ""),
            })
            .OrderByDescending(x => x.preferred)
            .ThenBy(x => x.displayName)
            .ToList();
        return Results.Json(rich);
    }
    catch
    {
        return Results.Json(preferred.Select(n => new { name = n, displayName = n, preferred = true }));
    }
});

app.MapPost("/api/create-resource-group", async (CreateRgBody body) =>
{
    if (string.IsNullOrWhiteSpace(body?.Name) || string.IsNullOrWhiteSpace(body.Location))
        return Results.Json(new { error = "name and location are required" }, statusCode: 400);
    try
    {
        var exists = await az.JsonAsync(new[] { "group", "exists", "--name", body.Name });
        if (exists is { ValueKind: JsonValueKind.True })
            return Results.Json(new { name = body.Name, location = body.Location, existed = true });
        var rg = await az.JsonAsync(new[] { "group", "create", "--name", body.Name, "--location", body.Location });
        return Results.Json(new { name = body.Name, location = body.Location, existed = false });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// --- Existing plans / web apps (for "use existing" pickers) -----------------
app.MapGet("/api/plans", async () =>
{
    try
    {
        var items = await az.JsonAsync(new[] { "appservice", "plan", "list" });
        if (items is null || items.Value.ValueKind != JsonValueKind.Array)
            return Results.Json(Array.Empty<object>());
        var mapped = items.Value.EnumerateArray()
            // Windows plans only (Linux plans set "reserved": true).
            .Where(a => !(a.TryGetProperty("reserved", out var r) && r.ValueKind == JsonValueKind.True))
            .Select(a => new
            {
                name = a.GetProperty("name").GetString(),
                resourceGroup = a.TryGetProperty("resourceGroup", out var rg) ? rg.GetString() : "",
                sku = a.TryGetProperty("sku", out var s) && s.TryGetProperty("name", out var sn) ? sn.GetString() : "",
                location = a.TryGetProperty("location", out var loc) ? loc.GetString() : "",
            });
        return Results.Json(mapped);
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapGet("/api/webapps", async (string? resourceGroup) =>
{
    try
    {
        var args = new List<string> { "webapp", "list" };
        if (!string.IsNullOrWhiteSpace(resourceGroup)) { args.Add("--resource-group"); args.Add(resourceGroup); }
        var items = await az.JsonAsync(args);
        if (items is null || items.Value.ValueKind != JsonValueKind.Array)
            return Results.Json(Array.Empty<object>());
        var mapped = items.Value.EnumerateArray()
            .Where(a => !(a.TryGetProperty("reserved", out var r) && r.ValueKind == JsonValueKind.True))
            .Select(a => new
            {
                name = a.GetProperty("name").GetString(),
                resourceGroup = a.TryGetProperty("resourceGroup", out var rg) ? rg.GetString() : "",
            });
        return Results.Json(mapped);
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// --- Persisted deployment config -------------------------------------------
app.MapGet("/api/config", () =>
{
    try
    {
        if (File.Exists(configPath))
            return Results.Content(File.ReadAllText(configPath), "application/json");
    }
    catch { /* ignore */ }
    return Results.Json(new { });
});

app.MapPost("/api/save-config", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    try
    {
        await File.WriteAllTextAsync(configPath, body);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

// --- Saved deployment (app URL + API key) -------------------------------------
// The deployed app reads telemetry with its managed identity and serves the report through a secured API;
// the portal calls it with the saved app URL and API key.
JsonElement LoadConfig()
{
    try
    {
        if (File.Exists(configPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            return doc.RootElement.Clone();
        }
    }
    catch { /* ignore */ }
    return default;
}

(string? url, string? key) ResolveApp(string? appUrl, string? apiKey)
{
    if (!string.IsNullOrWhiteSpace(appUrl) && !string.IsNullOrWhiteSpace(apiKey))
        return (appUrl.TrimEnd('/'), apiKey);
    var cfg = LoadConfig();
    if (cfg.ValueKind == JsonValueKind.Object)
    {
        var u = cfg.TryGetProperty("appUrl", out var uu) ? uu.GetString() : null;
        var k = cfg.TryGetProperty("apiKey", out var kk) ? kk.GetString() : null;
        return (u?.TrimEnd('/'), k);
    }
    return (null, null);
}

// --- Foundry hierarchy report ------------------------------------------------
// The same engine the deployed app uses runs here with the signed-in Azure CLI identity, so discovery and the report
// work before anything is deployed. A fresh credential per request means a tenant switch never reuses an old token.
var sharedHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

static List<string> CleanSubscriptions(IEnumerable<string>? ids) =>
    (ids ?? Enumerable.Empty<string>()).Where(s => Guid.TryParse(s, out _)).Select(s => s.ToLowerInvariant()).Distinct().ToList();

static string Short(Exception ex)
{
    var line = ex.Message.Split('\n')[0].Trim();
    return line.Length > 400 ? line[..400] + "..." : line;
}

// Inclusive UTC dates -> query window; a range that reaches today ends at the moment of the request.
static (DateTimeOffset Start, DateTimeOffset End, string? Error) Window(DateOnly? startDate, DateOnly? endDate)
{
    if (startDate is null || endDate is null) return (default, default, "Both startDate and endDate are required.");
    if (startDate > endDate) return (default, default, "startDate must be on or before endDate.");
    var start = new DateTimeOffset(startDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    var end = new DateTimeOffset(endDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    if (end > DateTimeOffset.UtcNow) end = DateTimeOffset.UtcNow;
    return start >= end ? (default, default, "The selected date range is in the future.") : (start, end, null);
}

// Step 3 of the install wizard: what Foundry resources exist in the selected subscriptions.
app.MapPost("/api/discover", async (DiscoverBody? body, CancellationToken ct) =>
{
    var subs = CleanSubscriptions(body?.SubscriptionIds);
    if (subs.Count == 0) return Results.Json(new { error = "Select at least one subscription." }, statusCode: 400);
    try
    {
        var credential = new CachedCredential(new AzureCliCredential());
        return Results.Json(await new FoundryDiscovery(new ArmClient(credential, sharedHttp)).DiscoverAsync(subs, includeAgents: true, ct));
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Json(new { error = Short(ex) }, statusCode: 500); }
});

// "Live from Azure": the full report using the signed-in user's access, no deployment needed.
app.MapPost("/api/usage", async (UsageBody? body, CancellationToken ct) =>
{
    var subs = CleanSubscriptions(body?.SubscriptionIds);
    if (subs.Count == 0) return Results.Json(new { error = "Select at least one subscription." }, statusCode: 400);
    var (start, end, error) = Window(body?.StartDate, body?.EndDate);
    if (error is not null) return Results.Json(new { error }, statusCode: 400);
    try
    {
        var overrides = PriceBook.ParseOverrides(Environment.GetEnvironmentVariable("REPORT_PRICE_OVERRIDES"));
        var credential = new CachedCredential(new AzureCliCredential());
        var service = new UsageReportService(new FoundryDiscovery(new ArmClient(credential, sharedHttp)), new UsageQueryRunner(credential), new PriceBook(sharedHttp, overrides));
        return Results.Json(await service.RunAsync(new ReportRequest(subs, start, end, body!.TenantId ?? "", body.TenantName ?? ""), ct));
    }
    catch (FormatException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400 ); }
    catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Json(new { error = Short(ex) }, statusCode: 500); }
});

// The deployed app's report (its managed identity and its REPORT_SUBSCRIPTIONS scope), proxied with the saved API key.
app.MapPost("/api/app-usage", async (AppUsageBody? body, CancellationToken ct) =>
{
    var (u, k) = ResolveApp(null, null);
    if (u is null || k is null) return Results.Json(new { error = "No deployed app is configured yet." }, statusCode: 400);
    var (_, _, error) = Window(body?.StartDate, body?.EndDate);
    if (error is not null) return Results.Json(new { error }, statusCode: 400);
    try
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(4);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{u}/api/usage?startDate={body!.StartDate:yyyy-MM-dd}&endDate={body.EndDate:yyyy-MM-dd}");
        req.Headers.Add("X-Api-Key", k);
        using var resp = await client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return Results.Json(new { error = "The deployed app does not provide the Foundry report. Redeploy it from this portal, or use 'Live from Azure'." }, statusCode: 404);
        return Results.Content(text, "application/json", statusCode: (int)resp.StatusCode);
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Json(new { error = Short(ex) }, statusCode: 502); }
});

// --- Prerequisites ---------------------------------------------------------
app.MapGet("/api/prerequisites", async () =>
{
    object azResult;
    try
    {
        var v = await az.JsonAsync(new[] { "version" }, timeoutMs: 15_000);
        string? version = v is { ValueKind: JsonValueKind.Object } && v.Value.TryGetProperty("azure-cli", out var e)
            ? e.GetString()
            : null;
        azResult = new { label = "Azure CLI", installed = true, version };
    }
    catch
    {
        azResult = new { label = "Azure CLI", installed = false, version = (string?)null };
    }

    return Results.Json(new { tools = new { azcli = azResult }, scriptFound = deployer.ScriptExists, scriptPath = deployer.ScriptPath });
});

// --- Deployment (streamed) -------------------------------------------------
app.MapPost("/api/deploy", async (HttpContext ctx, DeployRequest req) =>
{
    ctx.Response.ContentType = "text/plain; charset=utf-8";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    await foreach (var line in deployer.RunAsync(req, ctx.RequestAborted))
    {
        await ctx.Response.WriteAsync(line + "\n", Encoding.UTF8, ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }
});

app.MapPost("/api/shutdown", () =>
{
    _ = Task.Run(async () => { await Task.Delay(250); Environment.Exit(0); });
    return Results.Json(new { ok = true });
});

var url = Environment.GetEnvironmentVariable("PORTAL_URL") ?? "http://127.0.0.1:5099";
app.Run(url);

// --- Request DTOs ----------------------------------------------------------
record LoginBody(string? TenantId);
record SubscriptionBody(string? SubscriptionId);
record CreateRgBody(string? Name, string? Location);
record DiscoverBody(List<string>? SubscriptionIds);
record UsageBody(List<string>? SubscriptionIds, DateOnly? StartDate, DateOnly? EndDate, string? TenantId, string? TenantName);
record AppUsageBody(DateOnly? StartDate, DateOnly? EndDate);
