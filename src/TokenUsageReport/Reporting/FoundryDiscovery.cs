using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenUsageReport.Reporting;

public static partial class ArmIds
{
    [GeneratedRegex(@"^/subscriptions/(?<sub>[^/]+)/resourceGroups/(?<rg>[^/]+)/providers/[^/]+/(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string? id, out string subscription, out string resourceGroup, out string name)
    {
        subscription = resourceGroup = name = "";
        if (string.IsNullOrWhiteSpace(id)) return false;
        var m = Pattern().Match(id.TrimEnd('/'));
        if (!m.Success) return false;
        subscription = m.Groups["sub"].Value;
        resourceGroup = m.Groups["rg"].Value;
        name = m.Groups["rest"].Value.Split('/')[^1];
        return true;
    }

    /// <summary>".../accounts/a/projects/p" -> ".../accounts/a"; returns "" for ids that are not projects.</summary>
    public static string AccountOfProject(string projectId)
    {
        var i = projectId.IndexOf("/projects/", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? "" : projectId[..i];
    }

    public static bool SameId(string? a, string? b) => string.Equals(a?.TrimEnd('/'), b?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

public interface IFoundryDiscovery
{
    Task<DiscoveryResult> DiscoverAsync(IReadOnlyCollection<string> subscriptionIds, bool includeAgents, CancellationToken ct);
}

/// <summary>
/// Finds Foundry resources in the given subscriptions: resource groups, Foundry accounts and projects, the Application
/// Insights components linked through project/account connections, model deployments and agents.
/// </summary>
public sealed class FoundryDiscovery : IFoundryDiscovery
{
    private readonly ArmClient _arm;
    public FoundryDiscovery(ArmClient arm) => _arm = arm;

    public async Task<DiscoveryResult> DiscoverAsync(IReadOnlyCollection<string> subscriptionIds, bool includeAgents, CancellationToken ct)
    {
        var subs = subscriptionIds.Where(s => Guid.TryParse(s, out _)).Select(s => s.ToLowerInvariant()).Distinct().ToList();
        var result = new DiscoveryResult();
        if (subs.Count == 0)
        {
            result.Warnings.Add(new ReportWarning("Scope", "No subscriptions were selected."));
            return result;
        }

        var subTask = _arm.GraphAsync("resourcecontainers | where type =~ 'microsoft.resources/subscriptions' | project id = subscriptionId, name, tenantId", subs, ct);
        var rgTask = _arm.GraphAsync("resourcecontainers | where type =~ 'microsoft.resources/subscriptions/resourcegroups' | project subscriptionId, name, location", subs, ct);
        var accountTask = _arm.GraphAsync("resources | where type =~ 'microsoft.cognitiveservices/accounts' and kind =~ 'AIServices' | project id, name, subscriptionId, resourceGroup, location", subs, ct);
        var projectTask = _arm.GraphAsync("resources | where type =~ 'microsoft.cognitiveservices/accounts/projects' | project id, name, apiEndpoint = tostring(properties.endpoints['AI Foundry API'])", subs, ct);
        var insightsTask = _arm.GraphAsync("resources | where type =~ 'microsoft.insights/components' | project id, name, subscriptionId, resourceGroup, location", subs, ct);
        await Task.WhenAll(subTask, rgTask, accountTask, projectTask, insightsTask);

        var names = subTask.Result.ToDictionary(r => S(r, "id").ToLowerInvariant(), r => r, StringComparer.OrdinalIgnoreCase);
        foreach (var id in subs)
        {
            names.TryGetValue(id, out var row);
            result.Subscriptions.Add(new SubscriptionInfo(id, row.ValueKind == JsonValueKind.Object ? S(row, "name") : id, row.ValueKind == JsonValueKind.Object ? S(row, "tenantId") : ""));
        }
        result.TenantId = result.Subscriptions.Select(s => s.TenantId).FirstOrDefault(t => t != "") ?? "";
        result.ResourceGroups.AddRange(rgTask.Result
            .Select(r => new ResourceGroupInfo(S(r, "subscriptionId"), S(r, "name"), S(r, "location")))
            .OrderBy(r => r.SubscriptionId).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase));

        var accounts = accountTask.Result.Select(r => new FoundryAccount
        {
            Id = S(r, "id"), Name = S(r, "name"), SubscriptionId = S(r, "subscriptionId"),
            ResourceGroup = S(r, "resourceGroup"), Location = S(r, "location"),
        }).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var accountsById = accounts.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var projects = new List<(FoundryAccount Account, FoundryProject Project)>();
        foreach (var r in projectTask.Result.OrderBy(r => S(r, "name"), StringComparer.OrdinalIgnoreCase))
        {
            // Projects of non-Foundry (e.g. plain OpenAI) accounts are not part of this report.
            if (!accountsById.TryGetValue(ArmIds.AccountOfProject(S(r, "id")), out var account)) continue;
            // ARG names a child resource "account/project"; the project's own name is the last segment.
            var project = new FoundryProject { Id = S(r, "id"), Name = S(r, "name").Split('/')[^1], ApiEndpoint = S(r, "apiEndpoint") };
            account.Projects.Add(project);
            projects.Add((account, project));
        }

        var warningLock = new object();
        void Warn(string scope, string message) { lock (warningLock) result.Warnings.Add(new ReportWarning(scope, message)); }

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct };
        await Parallel.ForEachAsync(accounts, parallel, async (account, token) =>
        {
            try { account.Deployments.AddRange(await ListDeploymentsAsync(account.Id, token)); }
            catch (ArmException ex) { Warn(account.Name, $"Could not list model deployments. {ex.Message}"); }
            try { account.AppInsightsIds.AddRange(await ListAppInsightsAsync(account.Id, token)); }
            catch (ArmException ex) { Warn(account.Name, $"Could not read connections. {ex.Message}"); }
        });

        await Parallel.ForEachAsync(projects, parallel, async (item, token) =>
        {
            var (account, project) = item;
            try { project.AppInsightsIds.AddRange(await ListAppInsightsAsync(project.Id, token)); }
            catch (ArmException ex) { Warn($"{account.Name}/{project.Name}", $"Could not read project connections. {ex.Message}"); }
            // The most specific link wins: a project's own connection, otherwise the account's.
            if (project.AppInsightsIds.Count == 0) project.AppInsightsIds.AddRange(account.AppInsightsIds);
            if (project.AppInsightsIds.Count == 0)
                Warn($"{account.Name}/{project.Name}", "No linked Application Insights connection was found, so its usage cannot be reported.");
            if (includeAgents) await LoadAgentsAsync(account, project, token, Warn);
        });

        result.Accounts.AddRange(accounts);

        var knownInsights = insightsTask.Result.ToDictionary(r => S(r, "id"), r => r, StringComparer.OrdinalIgnoreCase);
        var linked = accounts.SelectMany(a => a.Projects).SelectMany(p => p.AppInsightsIds)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(i => i, StringComparer.OrdinalIgnoreCase);
        foreach (var id in linked)
        {
            if (knownInsights.TryGetValue(id, out var row))
                result.AppInsights.Add(new AppInsightsInfo(S(row, "id"), S(row, "name"), S(row, "subscriptionId"), S(row, "resourceGroup"), S(row, "location")));
            else if (ArmIds.TryParse(id, out var sub, out var rg, out var name))
                result.AppInsights.Add(new AppInsightsInfo(id, name, sub, rg, ""));
        }
        return result;
    }

    private async Task<List<DeploymentInfo>> ListDeploymentsAsync(string accountId, CancellationToken ct)
    {
        var items = await _arm.ListAsync($"https://management.azure.com{accountId}/deployments?api-version=2024-10-01", ct);
        return items.Select(d => new DeploymentInfo(
            S(d, "name"), Nested(d, "properties", "model", "name"), Nested(d, "properties", "model", "version"),
            Nested(d, "sku", "name"), Nested(d, "sku", "capacity"))).ToList();
    }

    private async Task<List<string>> ListAppInsightsAsync(string parentId, CancellationToken ct)
    {
        var items = await _arm.ListAsync($"https://management.azure.com{parentId}/connections?api-version=2025-06-01", ct);
        var ids = new List<string>();
        foreach (var c in items)
        {
            if (!string.Equals(Nested(c, "properties", "category"), "AppInsights", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Nested(c, "properties", "metadata", "ResourceId");
            if (!id.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)) id = Nested(c, "properties", "target");
            if (id.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)) ids.Add(id);
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task LoadAgentsAsync(FoundryAccount account, FoundryProject project, CancellationToken ct, Action<string, string> warn)
    {
        // The bearer token is only ever sent to a Foundry project endpoint.
        if (!Uri.TryCreate(project.ApiEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            project.AgentsError = "No Foundry project endpoint is available.";
            return;
        }
        try
        {
            string? after = null;
            for (var page = 0; page < 20; page++)
            {
                var url = $"{project.ApiEndpoint.TrimEnd('/')}/agents?api-version=v1&limit=100" + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
                var body = await _arm.GetAsync(url, ct, ArmClient.FoundryScope);
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
                foreach (var a in data.EnumerateArray())
                    project.Agents.Add(new AgentInfo(S(a, "name"), S(a, "id"),
                        Nested(a, "versions", "latest", "definition", "kind"), Nested(a, "versions", "latest", "definition", "model"),
                        Nested(a, "versions", "latest", "version")));
                after = body.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True && body.TryGetProperty("last_id", out var last)
                    ? last.GetString() : null;
                if (after is null) break;
            }
        }
        catch (ArmException ex)
        {
            project.AgentsError = ex.Message;
            warn($"{account.Name}/{project.Name}", $"Could not list agents (usage is still reported from telemetry). {ex.Message}");
        }
    }

    internal static string S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString() : "";

    internal static string Nested(JsonElement e, params string[] path)
    {
        foreach (var segment in path)
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(segment, out e)) return "";
        return e.ValueKind switch { JsonValueKind.String => e.GetString() ?? "", JsonValueKind.Null or JsonValueKind.Undefined => "", _ => e.ToString() };
    }
}
