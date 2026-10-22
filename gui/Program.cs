using System.Text;
using System.Text.Json;
using DeployPortal.Services;

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
        "swedencentral", "uksouth", "francecentral", "australiaeast", "japaneast", "canadacentral",
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

// --- Application Insights components (for the dropdown) ---------------------
app.MapGet("/api/appinsights", async () =>
{
    try
    {
        var items = await az.JsonAsync(new[]
        {
            "resource", "list", "--resource-type", "microsoft.insights/components",
        });
        if (items is null || items.Value.ValueKind != JsonValueKind.Array)
            return Results.Json(Array.Empty<object>());
        var mapped = items.Value.EnumerateArray().Select(a => new
        {
            id = a.GetProperty("id").GetString(),
            name = a.GetProperty("name").GetString(),
            resourceGroup = a.TryGetProperty("resourceGroup", out var rg) ? rg.GetString() : "",
            location = a.TryGetProperty("location", out var loc) ? loc.GetString() : "",
        });
        return Results.Json(mapped);
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

app.MapGet("/api/sample-report", () =>
{
    // Mirrors the exact schema produced by the worker's CsvWriter, with realistic demo rows.
    var header = "SubscriptionId,DeploymentName,Backend,ModelName,AccountName,CogSvcSubscriptionId,AccountId,SkuName,SkuCapacity,PromptTokens,CompletionTokens,CachedTokens,TotalTokens,Calls";
    var rows = new[]
    {
        "a3b144fd-39ee-4729-b816-3ed42fc8f31f,gpt-4o,myaccount.openai.azure.com,gpt-4o,myaccount,a3b144fd-39ee-4729-b816-3ed42fc8f31f,/subscriptions/a3b144fd/resourceGroups/rg-ai/providers/Microsoft.CognitiveServices/accounts/myaccount,GlobalStandard,50,412860,128340,64210,541200,1820",
        "a3b144fd-39ee-4729-b816-3ed42fc8f31f,gpt-4o-mini,myaccount.openai.azure.com,gpt-4o-mini,myaccount,a3b144fd-39ee-4729-b816-3ed42fc8f31f,/subscriptions/a3b144fd/resourceGroups/rg-ai/providers/Microsoft.CognitiveServices/accounts/myaccount,GlobalStandard,30,238100,71540,18220,309640,3450",
        "a3b144fd-39ee-4729-b816-3ed42fc8f31f,text-embedding-3-large,myaccount.openai.azure.com,text-embedding-3-large,myaccount,a3b144fd-39ee-4729-b816-3ed42fc8f31f,/subscriptions/a3b144fd/resourceGroups/rg-ai/providers/Microsoft.CognitiveServices/accounts/myaccount,Standard,10,88400,0,0,88400,6120",
        "a3b144fd-39ee-4729-b816-3ed42fc8f31f,gpt-4o,foundryeast.cognitiveservices.azure.com,gpt-4o,foundryeast,a3b144fd-39ee-4729-b816-3ed42fc8f31f,/subscriptions/a3b144fd/resourceGroups/rg-ai2/providers/Microsoft.CognitiveServices/accounts/foundryeast,GlobalStandard,100,904220,301770,142880,1205990,4210",
    };
    var csv = header + "\n" + string.Join("\n", rows) + "\n";
    return Results.Json(new { name = "sample-token-usage-report.csv", csv });
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

// --- App-serves-reports proxy (portal never touches blob directly) ----------
// The deployed app reads/writes blobs with its managed identity and exposes a secured
// API; the portal calls the app (using the saved app URL + API key) to list/view reports.
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

async Task<IResult> ProxyGet(string appUrl, string apiKey, string path, CancellationToken ct)
{
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromMinutes(3);
    using var req = new HttpRequestMessage(HttpMethod.Get, $"{appUrl}{path}");
    req.Headers.Add("X-Api-Key", apiKey);
    using var resp = await client.SendAsync(req, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);
    return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
}

app.MapGet("/api/app-reports", async (string? appUrl, string? apiKey, CancellationToken ct) =>
{
    var (u, k) = ResolveApp(appUrl, apiKey);
    if (u is null || k is null) return Results.Json(new { error = "No deployed app is configured yet." }, statusCode: 400);
    try { return await ProxyGet(u, k, "/api/reports", ct); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});

app.MapGet("/api/app-report", async (string name, string? appUrl, string? apiKey, CancellationToken ct) =>
{
    var (u, k) = ResolveApp(appUrl, apiKey);
    if (u is null || k is null) return Results.Json(new { error = "No deployed app is configured yet." }, statusCode: 400);
    try { return await ProxyGet(u, k, $"/api/reports/content?name={Uri.EscapeDataString(name)}", ct); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});

app.MapPost("/api/app-run", async (AppRunBody? body, CancellationToken ct) =>
{
    var (u, k) = ResolveApp(body?.AppUrl, body?.ApiKey);
    if (u is null || k is null) return Results.Json(new { error = "No deployed app is configured yet." }, statusCode: 400);
    if (body?.StartDate is null || body.EndDate is null)
        return Results.Json(new { error = "Both startDate and endDate are required." }, statusCode: 400);
    if (body.StartDate > body.EndDate)
        return Results.Json(new { error = "startDate must be on or before endDate." }, statusCode: 400);
    try
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(4);
        var startUtc = body.StartDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endUtc = body.EndDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var todayMidnightUtc = DateTime.UtcNow.Date;
        var lookbackHours = (int)(endUtc - startUtc).TotalHours;
        var endOffsetHours = (int)(todayMidnightUtc - endUtc).TotalHours;
        var path = $"/api/run?startDate={body.StartDate:yyyy-MM-dd}&endDate={body.EndDate:yyyy-MM-dd}" +
            $"&lookbackHours={lookbackHours}&endOffsetHours={endOffsetHours}";
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{u}{path}");
        req.Headers.Add("X-Api-Key", k);
        using var resp = await client.SendAsync(req, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        return Results.Content(respBody, "application/json", statusCode: (int)resp.StatusCode);
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
});

// --- Report query (KQL) templates + validation + APIM link detection ----------------------
var queriesDir = Path.Combine(Directory.GetParent(app.Environment.ContentRootPath)?.FullName ?? app.Environment.ContentRootPath,
    "src", "TokenUsageReport", "Queries");
var queryMeta = new Dictionary<string, (string Name, string Description)>(StringComparer.OrdinalIgnoreCase)
{
    ["apim-token-metrics"] = ("APIM token metrics (default)",
        "Reads Prompt/Completion/Cached/Total Tokens metrics emitted by the APIM llm-emit-token-metric policy."),
    ["foundry-genai"] = ("Foundry / GenAI telemetry (starting point)",
        "Reads OpenTelemetry gen_ai.* spans from Foundry agents / Azure OpenAI SDK tracing. Verify against your data."),
};
string[] requiredColumns = { "SubscriptionId", "DeploymentName", "Backend", "PromptTokens", "CompletionTokens", "CachedTokens", "TotalTokens", "Calls" };

app.MapGet("/api/queries", () =>
{
    try
    {
        var list = Directory.Exists(queriesDir)
            ? Directory.GetFiles(queriesDir, "*.kql").OrderBy(f => f.Contains("apim-token-metrics") ? 0 : 1).Select(f =>
            {
                var id = Path.GetFileNameWithoutExtension(f);
                queryMeta.TryGetValue(id, out var m);
                return new { id, name = m.Name ?? id, description = m.Description ?? "", query = File.ReadAllText(f) };
            }).ToList()
            : null;
        return Results.Json(new { queries = list, required = requiredColumns });
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
});

app.MapPost("/api/validate-query", async (ValidateQueryBody body) =>
{
    if (string.IsNullOrWhiteSpace(body?.AppInsightsResourceId) || string.IsNullOrWhiteSpace(body.Query))
        return Results.Json(new { error = "An Application Insights component and a query are required." }, statusCode: 400);
    if (!body.AppInsightsResourceId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
        || body.AppInsightsResourceId.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '"', ' ', '?' }) >= 0)
        return Results.Json(new { error = "Invalid Application Insights resource id." }, statusCode: 400);

    var bodyFile = Path.Combine(Path.GetTempPath(), $"tokrpt-q-{Guid.NewGuid():N}.json");
    try
    {
        await File.WriteAllTextAsync(bodyFile, JsonSerializer.Serialize(new { query = body.Query, timespan = "P30D" }));
        var res = await az.JsonAsync(new[] { "rest", "--method", "post",
            "--url", $"https://management.azure.com{body.AppInsightsResourceId}/api/query?api-version=2014-12-01-preview",
            "--body", $"@{bodyFile}" }, timeoutMs: 90_000);

        var cols = new List<string>();
        var rowCount = 0;
        if (res is { ValueKind: JsonValueKind.Object } r)
        {
            JsonElement tables = default;
            var has = r.TryGetProperty("Tables", out tables) || r.TryGetProperty("tables", out tables);
            if (has && tables.ValueKind == JsonValueKind.Array && tables.GetArrayLength() > 0)
            {
                var t = tables[0];
                if ((t.TryGetProperty("Columns", out var c) || t.TryGetProperty("columns", out c)) && c.ValueKind == JsonValueKind.Array)
                    foreach (var col in c.EnumerateArray())
                        cols.Add((col.TryGetProperty("ColumnName", out var n) || col.TryGetProperty("name", out n)) ? n.GetString() ?? "" : "");
                if ((t.TryGetProperty("Rows", out var rw) || t.TryGetProperty("rows", out rw)) && rw.ValueKind == JsonValueKind.Array)
                    rowCount = rw.GetArrayLength();
            }
        }
        var missing = requiredColumns.Where(rc => !cols.Contains(rc, StringComparer.OrdinalIgnoreCase)).ToArray();
        return Results.Json(new { ok = true, columns = cols, missing, rowCount });
    }
    catch (Exception ex)
    {
        var msg = ex.Message;
        var m = System.Text.RegularExpressions.Regex.Match(msg, "\"innererror\":\\{\"code\":\"(\\w+)\",\"message\":\"([^\"]*)\"");
        if (m.Success) msg = $"KQL error: {m.Groups[2].Value} ({m.Groups[1].Value}). Check the query syntax.";
        else if (msg.Length > 600) msg = msg[..600] + "…";
        return Results.Json(new { ok = false, error = msg });
    }
    finally { try { File.Delete(bodyFile); } catch { /* ignore */ } }
});

// Which Application Insights does each APIM instance log to? Those are the components that receive llm-emit-token-metric data.
app.MapGet("/api/apim-links", async () =>
{
    var links = new List<object>();
    try
    {
        var apims = await az.JsonAsync(new[] { "apim", "list" });
        if (apims is { ValueKind: JsonValueKind.Array })
            foreach (var a in apims.Value.EnumerateArray())
            {
                var id = a.TryGetProperty("id", out var i) ? i.GetString() : null;
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                try
                {
                    var loggers = await az.JsonAsync(new[] { "rest", "--method", "get",
                        "--url", $"https://management.azure.com{id}/loggers?api-version=2022-08-01" }, timeoutMs: 30_000);
                    if (loggers is { ValueKind: JsonValueKind.Object } l && l.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
                        foreach (var lg in v.EnumerateArray())
                        {
                            if (!lg.TryGetProperty("properties", out var p)) continue;
                            var type = p.TryGetProperty("loggerType", out var lt) ? lt.GetString() : null;
                            var res = p.TryGetProperty("resourceId", out var rid) ? rid.GetString() : null;
                            if (string.Equals(type, "applicationInsights", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(res))
                                links.Add(new { apim = name, appInsightsId = res });
                        }
                }
                catch { /* this APIM's loggers are not readable — skip it */ }
            }
    }
    catch { /* az apim not available / no access — return what we have */ }
    return Results.Json(links);
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
record AppRunBody(string? AppUrl, string? ApiKey, DateOnly? StartDate, DateOnly? EndDate);
record ValidateQueryBody(string? AppInsightsResourceId, string? Query);
