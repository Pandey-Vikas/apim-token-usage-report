namespace TokenUsageReport.Models;

/// <summary>A token-usage row returned by the Application Insights query.</summary>
public sealed class TokenUsageRow
{
    public string SubscriptionId { get; set; } = "";
    public string DeploymentName { get; set; } = "";
    public string Backend { get; set; } = "";
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long CachedTokens { get; set; }
    public long TotalTokens { get; set; }
    public long Calls { get; set; }
}
