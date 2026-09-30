namespace TokenUsageReport.Models;

/// <summary>A flattened Cognitive Services deployment used to enrich token rows.</summary>
public sealed class InventoryItem
{
    public string Backend { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string SkuName { get; set; } = "";
    public string SkuCapacity { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string CogSvcSubscriptionId { get; set; } = "";
    public string AccountId { get; set; } = "";

    /// <summary>Join key used to match a token row (Backend + DeploymentName).</summary>
    public string Key => MakeKey(Backend, DeploymentName);

    public static string MakeKey(string backend, string deploymentName) =>
        $"{backend}|{deploymentName}".ToLowerInvariant();
}

/// <summary>The final report row: token usage joined with inventory metadata.</summary>
public sealed class EnrichedRow
{
    public string SubscriptionId { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string Backend { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string CogSvcSubscriptionId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string SkuName { get; set; } = "";
    public string SkuCapacity { get; set; } = "";
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
    public long TotalTokens { get; set; }
    public long Calls { get; set; }
}
