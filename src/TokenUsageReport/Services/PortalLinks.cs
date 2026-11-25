using System.Text.RegularExpressions;

namespace TokenUsageReport.Services;

/// <summary>Builds Azure portal deep links from ARM resource ids.</summary>
public static partial class PortalLinks
{
    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[A-Za-z0-9._()-]+/providers/[A-Za-z0-9._()/-]+$")]
    private static partial Regex ArmId();

    /// <summary>Returns the portal overview URL, or "" when the value is not a well-formed ARM resource id.</summary>
    public static string ForResource(string? resourceId) =>
        !string.IsNullOrWhiteSpace(resourceId) && ArmId().IsMatch(resourceId)
            ? $"https://portal.azure.com/#resource{resourceId}/overview"
            : "";

    /// <summary>Prefers the resource named by telemetry (e.g. the Foundry project), else the matched Cognitive Services account.</summary>
    public static (string ResourceId, string PortalUrl) Choose(string? telemetryResourceId, string? inventoryAccountId)
    {
        var url = ForResource(telemetryResourceId);
        if (url != "") return (telemetryResourceId!, url);
        url = ForResource(inventoryAccountId);
        return url == "" ? ("", "") : (inventoryAccountId!, url);
    }
}
