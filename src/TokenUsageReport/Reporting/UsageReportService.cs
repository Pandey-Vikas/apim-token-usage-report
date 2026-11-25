using System.Collections.Concurrent;
using System.Globalization;
using TokenUsageReport.Services;

namespace TokenUsageReport.Reporting;

public sealed record ReportRequest(IReadOnlyCollection<string> Subscriptions, DateTimeOffset Start, DateTimeOffset End, string TenantId, string TenantName);

/// <summary>
/// Builds the hierarchical usage report: discovers Foundry resources in the selected subscriptions, queries every linked
/// Application Insights component, attributes each usage row to its subscription / resource group / Foundry project /
/// agent / model, and prices it. A component that cannot be queried becomes a warning, never a silently empty report.
/// </summary>
public sealed class UsageReportService
{
    private readonly IFoundryDiscovery _discovery;
    private readonly IUsageSource _usage;
    private readonly PriceBook _prices;

    public UsageReportService(IFoundryDiscovery discovery, IUsageSource usage, PriceBook prices)
    {
        _discovery = discovery;
        _usage = usage;
        _prices = prices;
    }

    public async Task<UsageReport> RunAsync(ReportRequest request, CancellationToken ct)
    {
        var discovery = await _discovery.DiscoverAsync(request.Subscriptions, includeAgents: true, ct);
        var warnings = new List<ReportWarning>(discovery.Warnings);
        discovery.Warnings.Clear();
        if (string.IsNullOrEmpty(discovery.TenantId)) discovery.TenantId = request.TenantId;

        var results = new ConcurrentDictionary<string, List<UsageRow>>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(discovery.AppInsights, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (ai, token) =>
        {
            try { results[ai.Id] = await _usage.QueryAsync(ai.Id, request.Start, request.End, token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (warnings)
                    warnings.Add(new ReportWarning(ai.Name, $"Could not query this Application Insights. {FirstLine(ex.Message)} " +
                        "If access was denied, grant the report identity the Monitoring Reader role on it."));
            }
        });

        var subscriptionNames = discovery.Subscriptions.ToDictionary(s => s.Id, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var inScope = new HashSet<string>(discovery.Subscriptions.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        var projects = discovery.Accounts.SelectMany(a => a.Projects.Select(p => (Account: a, Project: p))).ToList();
        var projectById = projects.GroupBy(x => x.Project.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var scopes = new List<ReportScope>();
        var scopeIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(UsageRow Row, int Scope, FoundryAccount? Account)>();
        long skipped = 0, unattributed = 0;

        foreach (var ai in discovery.AppInsights)
        {
            if (!results.TryGetValue(ai.Id, out var rows)) continue;
            var linked = projects.Where(x => x.Project.AppInsightsIds.Contains(ai.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var row in rows)
            {
                FoundryAccount? account = null;
                ReportScope scope;
                string key;
                if (row.ProjectId != "" && projectById.TryGetValue(row.ProjectId, out var hit))
                {
                    account = hit.Account;
                    key = hit.Project.Id + "|" + ai.Id;
                    scope = Scope(hit.Account.SubscriptionId, hit.Account.ResourceGroup, hit.Account.Name, hit.Project.Name, hit.Project.Id, ai);
                }
                else if (row.ProjectId == "" && linked.Count == 1)
                {
                    account = linked[0].Account;
                    key = linked[0].Project.Id + "|" + ai.Id;
                    scope = Scope(account.SubscriptionId, account.ResourceGroup, account.Name, linked[0].Project.Name, linked[0].Project.Id, ai);
                }
                else if (row.ProjectId != "")
                {
                    // A project that is not in the discovered inventory (for example deleted). Keep it only inside the selected scope.
                    if (!ArmIds.TryParse(row.ProjectId, out var sub, out var rg, out var projectName) || !inScope.Contains(sub)) { skipped += row.Calls; continue; }
                    var accountId = ArmIds.AccountOfProject(row.ProjectId);
                    ArmIds.TryParse(accountId, out _, out _, out var accountName);
                    key = row.ProjectId + "|" + ai.Id;
                    scope = Scope(sub, rg, accountName == "" ? "(unknown)" : accountName, projectName, row.ProjectId, ai);
                }
                else
                {
                    unattributed += row.Calls;
                    key = "unattributed|" + ai.Id;
                    scope = Scope(ai.SubscriptionId, ai.ResourceGroup, "(unattributed)", "(unattributed)", "", ai);
                }
                if (!scopeIndex.TryGetValue(key, out var index))
                {
                    index = scopes.Count;
                    scopes.Add(Named(scope, subscriptionNames));
                    scopeIndex[key] = index;
                }
                pending.Add((row, index, account));
            }
        }
        if (skipped > 0)
            warnings.Add(new ReportWarning("Scope", $"{skipped:N0} model call(s) from Foundry projects outside the selected subscriptions were left out, although they share a linked Application Insights."));
        if (unattributed > 0)
            warnings.Add(new ReportWarning("Attribution", $"{unattributed:N0} model call(s) carry no Foundry project id and their Application Insights is shared by several projects, so they appear as (unattributed)."));

        var (facts, notes) = await PriceAsync(pending, ct);
        if (facts.Any(f => f.CostCurrency != "" && !f.CostCurrency.Equals("USD", StringComparison.OrdinalIgnoreCase)))
            warnings.Add(new ReportWarning("Cost", "Some telemetry reports costs in a currency other than USD; totals mix currencies. Estimated costs are USD list prices."));

        return new UsageReport
        {
            Tenant = new TenantInfo(discovery.TenantId, request.TenantName),
            WindowStartUtc = request.Start,
            WindowEndUtc = request.End,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Inventory = discovery,
            Scopes = scopes,
            Facts = facts.OrderBy(f => f.Day, StringComparer.Ordinal).ThenBy(f => f.Scope).ThenBy(f => f.AgentName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Model, StringComparer.OrdinalIgnoreCase).ToList(),
            Prices = notes,
            Warnings = warnings,
        };
    }

    private async Task<(List<UsageFact> Facts, List<PriceNote> Notes)> PriceAsync(List<(UsageRow Row, int Scope, FoundryAccount? Account)> pending, CancellationToken ct)
    {
        (string Deployment, string Base, string Version, string Sku, string SkuKind, string Region) Resolve(UsageRow row, FoundryAccount? account)
        {
            var (modelBase, version) = PriceBook.SplitModel(row.Model);
            var deployment = FindDeployment(account, row.Deployment, modelBase, version);
            if (deployment is not null)
            {
                if (deployment.Model != "") modelBase = deployment.Model;
                if (version == "") version = deployment.Version;
            }
            return (row.Deployment, modelBase, version, deployment?.Sku ?? "", PriceBook.SkuKindOf(deployment?.Sku), account?.Location ?? "");
        }

        var needed = pending.Select(p => Resolve(p.Row, p.Account)).Distinct().ToList();
        var priceTasks = needed.ToDictionary(n => n, n => _prices.GetAsync(n.Deployment, n.Base, n.Version, n.SkuKind, n.Region == "" ? null : n.Region, ct));
        await Task.WhenAll(priceTasks.Values);

        var facts = new List<UsageFact>();
        var notes = new Dictionary<string, PriceNote>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, scope, account) in pending)
        {
            var resolved = Resolve(row, account);
            var price = priceTasks[resolved].Result;
            decimal? estimated = price is null ? null : PriceBook.Cost(price, row.PromptTokens, row.CachedTokens, row.CompletionTokens);
            if (price is not null)
                notes[$"{resolved.Deployment}|{resolved.Sku}"] = new PriceNote(resolved.Deployment == "" ? resolved.Base : resolved.Deployment, resolved.Sku, price.InputPer1M, price.CachedInputPer1M, price.OutputPer1M, price.Source);
            facts.Add(new UsageFact
            {
                Day = row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Scope = scope, AgentId = row.AgentId, AgentName = row.AgentName, Model = row.Model, Deployment = row.Deployment, Sku = resolved.Sku,
                PromptTokens = row.PromptTokens, CompletionTokens = row.CompletionTokens, CachedTokens = row.CachedTokens,
                TotalTokens = row.PromptTokens + row.CompletionTokens, Calls = row.Calls, Errors = row.Errors, DurationMsSum = row.DurationMsSum,
                ReportedCost = row.ReportedCost, EstimatedCost = estimated, CostCurrency = row.CostCurrency,
                CostBasis = row.ReportedCost is not null ? "reported" : estimated is null ? "" : price!.Source.StartsWith("Custom") ? "custom" : "list",
                FirstSeen = row.FirstSeen?.ToString("o", CultureInfo.InvariantCulture), LastSeen = row.LastSeen?.ToString("o", CultureInfo.InvariantCulture),
            });
        }
        return (facts, notes.Values.OrderBy(n => n.Model, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Foundry telemetry may carry the deployment name or only the model name. Match the deployment by name first, then by model;
    /// when several deployments of the same model differ in SKU the price is ambiguous, so none is chosen.
    /// </summary>
    internal static DeploymentInfo? FindDeployment(FoundryAccount? account, string deploymentName, string modelBase, string version)
    {
        if (account is null) return null;
        var byName = account.Deployments.FirstOrDefault(d => d.Name.Equals(deploymentName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        var byModel = account.Deployments
            .Where(d => d.Model.Equals(modelBase, StringComparison.OrdinalIgnoreCase) && (version == "" || d.Version == "" || d.Version == version))
            .ToList();
        return byModel.Select(d => d.Sku).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? byModel[0] : null;
    }

    private static ReportScope Scope(string subscription, string resourceGroup, string account, string project, string projectId, AppInsightsInfo ai) => new()
    {
        SubscriptionId = subscription, ResourceGroup = resourceGroup, AccountName = account, ProjectName = project, ProjectId = projectId,
        AppInsightsId = ai.Id, AppInsightsName = ai.Name, PortalUrl = PortalLinks.ForResource(projectId), AppInsightsPortalUrl = PortalLinks.ForResource(ai.Id),
    };

    private static ReportScope Named(ReportScope s, Dictionary<string, string> names) => new()
    {
        SubscriptionId = s.SubscriptionId, SubscriptionName = names.TryGetValue(s.SubscriptionId, out var n) ? n : s.SubscriptionId,
        ResourceGroup = s.ResourceGroup, AccountName = s.AccountName, ProjectName = s.ProjectName, ProjectId = s.ProjectId,
        AppInsightsId = s.AppInsightsId, AppInsightsName = s.AppInsightsName, PortalUrl = s.PortalUrl, AppInsightsPortalUrl = s.AppInsightsPortalUrl,
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 300 ? line[..300] + "..." : line;
    }
}
