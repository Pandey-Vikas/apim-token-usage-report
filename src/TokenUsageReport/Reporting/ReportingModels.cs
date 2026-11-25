namespace TokenUsageReport.Reporting;

public sealed record SubscriptionInfo(string Id, string Name, string TenantId);

public sealed record ResourceGroupInfo(string SubscriptionId, string Name, string Location);

public sealed record DeploymentInfo(string Name, string Model, string Version, string Sku, string Capacity);

public sealed record AgentInfo(string Name, string Id, string Kind, string Model, string Version);

public sealed record AppInsightsInfo(string Id, string Name, string SubscriptionId, string ResourceGroup, string Location);

public sealed record ReportWarning(string Scope, string Message);

public sealed class FoundryProject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ApiEndpoint { get; init; } = "";
    public List<string> AppInsightsIds { get; init; } = new();
    public List<AgentInfo> Agents { get; init; } = new();
    public string? AgentsError { get; set; }
}

public sealed class FoundryAccount
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string SubscriptionId { get; init; } = "";
    public string ResourceGroup { get; init; } = "";
    public string Location { get; init; } = "";
    public List<DeploymentInfo> Deployments { get; init; } = new();
    public List<FoundryProject> Projects { get; init; } = new();
    public List<string> AppInsightsIds { get; init; } = new();
}

public sealed class DiscoveryResult
{
    public string TenantId { get; set; } = "";
    public List<SubscriptionInfo> Subscriptions { get; init; } = new();
    public List<ResourceGroupInfo> ResourceGroups { get; init; } = new();
    public List<FoundryAccount> Accounts { get; init; } = new();
    public List<AppInsightsInfo> AppInsights { get; init; } = new();
    public List<ReportWarning> Warnings { get; init; } = new();
}

/// <summary>One day of usage for an agent/model, as returned by the usage query.</summary>
public sealed record UsageRow(
    DateTime Day, string ProjectId, string AgentId, string AgentName, string Model, string Deployment, string Backend,
    long PromptTokens, long CompletionTokens, long CachedTokens, long Calls, long Errors, double DurationMsSum,
    decimal? ReportedCost, string CostCurrency, DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen);

/// <summary>A (Foundry project, Application Insights) pair that usage rows belong to; facts reference it by index.</summary>
public sealed class ReportScope
{
    public string SubscriptionId { get; init; } = "";
    public string SubscriptionName { get; init; } = "";
    public string ResourceGroup { get; init; } = "";
    public string AccountName { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string AppInsightsId { get; init; } = "";
    public string AppInsightsName { get; init; } = "";
    public string PortalUrl { get; init; } = "";
    public string AppInsightsPortalUrl { get; init; } = "";
}

public sealed class UsageFact
{
    public string Day { get; init; } = "";
    public int Scope { get; init; }
    public string AgentId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string Model { get; init; } = "";
    public string Deployment { get; init; } = "";
    public string Sku { get; init; } = "";
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long CachedTokens { get; init; }
    public long TotalTokens { get; init; }
    public long Calls { get; init; }
    public long Errors { get; init; }
    public double DurationMsSum { get; init; }
    public decimal? ReportedCost { get; init; }
    public decimal? EstimatedCost { get; init; }
    public string CostCurrency { get; init; } = "";
    public string CostBasis { get; init; } = "";
    public string? FirstSeen { get; init; }
    public string? LastSeen { get; init; }
}

public sealed record PriceNote(string Model, string Sku, decimal InputPer1M, decimal? CachedInputPer1M, decimal OutputPer1M, string Source);

public sealed record TenantInfo(string Id, string Name);

public sealed class UsageReport
{
    public TenantInfo Tenant { get; init; } = new("", "");
    public DateTimeOffset WindowStartUtc { get; init; }
    public DateTimeOffset WindowEndUtc { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public DiscoveryResult Inventory { get; init; } = new();
    public List<ReportScope> Scopes { get; init; } = new();
    public List<UsageFact> Facts { get; init; } = new();
    public List<PriceNote> Prices { get; init; } = new();
    public List<ReportWarning> Warnings { get; init; } = new();
}
