using System.Net;
using System.Text.Json;
using TokenUsageReport.Reporting;
using Xunit;

namespace TokenUsageReport.Tests;

public sealed class DiscoveryTests
{
    private const string Sub = "a3b144fd-39ee-4729-b816-3ed42fc8f31f";
    private static readonly string AccountId = $"/subscriptions/{Sub}/resourceGroups/rg1/providers/Microsoft.CognitiveServices/accounts/acct1";
    private static readonly string P1 = AccountId + "/projects/p1";
    private static readonly string P2 = AccountId + "/projects/p2";
    private static readonly string AiAccount = $"/subscriptions/{Sub}/resourceGroups/obs/providers/microsoft.insights/components/ai-acct";
    private static readonly string AiP1 = $"/subscriptions/{Sub}/resourceGroups/obs/providers/microsoft.insights/components/ai-p1";

    private static (HttpStatusCode, string) Ok(object body) => (HttpStatusCode.OK, JsonSerializer.Serialize(body));
    private static (HttpStatusCode, string) Graph(params object[] rows) => Ok(new { data = rows });

    private static object Connections(string? aiId) => new
    {
        value = aiId is null ? Array.Empty<object>() : new object[]
        {
            new { name = "ai", properties = new { category = "AppInsights", target = aiId, metadata = new { ResourceId = aiId } } },
            new { name = "search", properties = new { category = "CognitiveSearch", target = "https://search.example" } },
        },
    };

    private sealed record Options(bool ConnectionsForbidden = false, bool AgentsForbidden = false, string Endpoint1 = "https://acct1.services.ai.azure.com/api/projects/p1", bool ProjectConnection = true);

    private static FakeHttp Handler(Options o) => new((req, body) =>
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("ResourceGraph"))
        {
            if (body.Contains("accounts/projects"))
                return Graph(new { id = P1, name = "p1", apiEndpoint = o.Endpoint1 }, new { id = P2, name = "p2", apiEndpoint = "https://acct1.services.ai.azure.com/api/projects/p2" },
                    new { id = $"/subscriptions/{Sub}/resourceGroups/rg1/providers/Microsoft.CognitiveServices/accounts/plain-openai/projects/x", name = "x", apiEndpoint = "" });
            if (body.Contains("AIServices"))
                return Graph(new { id = AccountId, name = "acct1", subscriptionId = Sub, resourceGroup = "rg1", location = "eastus2" });
            if (body.Contains("insights/components"))
                return Graph(new { id = AiP1, name = "ai-p1", subscriptionId = Sub, resourceGroup = "obs", location = "eastus2" });
            if (body.Contains("resourcegroups"))
                return Graph(new { subscriptionId = Sub, name = "rg1", location = "eastus2" }, new { subscriptionId = Sub, name = "obs", location = "westeurope" });
            return Graph(new { id = Sub, name = "Prod", tenantId = "Tenant-1" });
        }
        if (url.Contains("/connections"))
        {
            if (o.ConnectionsForbidden) return (HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"no access"}}""");
            if (url.Contains("/projects/p1/")) return Ok(Connections(o.ProjectConnection ? AiP1 : null));
            if (url.Contains("/projects/")) return Ok(Connections(null));
            return Ok(Connections(AiAccount));
        }
        if (url.Contains("/deployments"))
            return Ok(new { value = new[] { new { name = "gpt-4o-mini", sku = new { name = "GlobalStandard", capacity = 50 }, properties = new { model = new { name = "gpt-4o-mini", version = "2024-07-18" } } } } });
        if (url.Contains("/agents"))
        {
            if (o.AgentsForbidden) return (HttpStatusCode.Forbidden, """{"error":{"code":"PermissionDenied","message":"agents denied"}}""");
            if (url.Contains("after=a1")) return Ok(new { data = new[] { new { id = "a2", name = "Beta" } }, has_more = false });
            if (url.Contains("/p1/"))
                return Ok(new { data = new[] { new { id = "a1", name = "Alpha", versions = new { latest = new { version = "3", definition = new { kind = "prompt", model = "gpt-4o" } } } } }, has_more = true, last_id = "a1" });
            return Ok(new { data = Array.Empty<object>(), has_more = false });
        }
        return (HttpStatusCode.NotFound, "{}");
    });

    private static async Task<(DiscoveryResult Result, FakeHttp Http)> Run(Options o, bool agents = true)
    {
        var http = Handler(o);
        var discovery = new FoundryDiscovery(new ArmClient(new FakeCredential(), new HttpClient(http)));
        return (await discovery.DiscoverAsync(new[] { Sub, "not-a-guid", Sub.ToUpperInvariant() }, agents, CancellationToken.None), http);
    }

    [Fact]
    public async Task BuildsTheHierarchyWithLinkedInsightsDeploymentsAndPagedAgents()
    {
        var (r, _) = await Run(new Options());
        Assert.Equal("Tenant-1", r.TenantId);
        Assert.Equal("Prod", Assert.Single(r.Subscriptions).Name);
        Assert.Equal(new[] { "obs", "rg1" }, r.ResourceGroups.Select(g => g.Name).OrderBy(n => n));
        var account = Assert.Single(r.Accounts);
        Assert.Equal(new[] { "p1", "p2" }, account.Projects.Select(p => p.Name)); // the plain-OpenAI account's project is not a Foundry project
        Assert.Equal("GlobalStandard", Assert.Single(account.Deployments).Sku);

        var p1 = account.Projects[0];
        var p2 = account.Projects[1];
        Assert.Equal(new[] { AiP1 }, p1.AppInsightsIds);       // a project's own connection wins
        Assert.Equal(new[] { AiAccount }, p2.AppInsightsIds);  // otherwise it inherits the account's
        Assert.Equal(new[] { "Alpha", "Beta" }, p1.Agents.Select(a => a.Name)); // second page followed
        Assert.Equal("gpt-4o", p1.Agents[0].Model);
        Assert.Equal("3", p1.Agents[0].Version);
        Assert.Equal(new[] { "ai-acct", "ai-p1" }, r.AppInsights.Select(a => a.Name).OrderBy(n => n));
        Assert.Equal("obs", r.AppInsights.Single(a => a.Name == "ai-acct").ResourceGroup); // parsed from the id when ARG cannot see it
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public async Task DuplicateAndInvalidSubscriptionIdsAreIgnoredAndAgentsCanBeSkipped()
    {
        var (r, http) = await Run(new Options(), agents: false);
        Assert.Single(r.Subscriptions);
        Assert.DoesNotContain(http.Calls, c => c.Contains("/agents"));
        Assert.All(r.Accounts.SelectMany(a => a.Projects), p => Assert.Empty(p.Agents));
    }

    [Fact]
    public async Task ForbiddenCallsBecomeWarningsInsteadOfFailingDiscovery()
    {
        var (r, _) = await Run(new Options(ConnectionsForbidden: true, AgentsForbidden: true));
        var account = Assert.Single(r.Accounts);
        Assert.Equal(2, account.Projects.Count);
        Assert.Contains(r.Warnings, w => w.Scope == "acct1" && w.Message.Contains("Could not read connections") && w.Message.Contains("403"));
        Assert.Contains(r.Warnings, w => w.Scope == "acct1/p1" && w.Message.Contains("No linked Application Insights"));
        Assert.Contains(r.Warnings, w => w.Scope == "acct1/p1" && w.Message.Contains("Could not list agents") && w.Message.Contains("agents denied"));
        Assert.Contains("agents denied", account.Projects[0].AgentsError);
        Assert.Empty(r.AppInsights);
    }

    [Fact]
    public async Task NeverSendsTheFoundryTokenToANonFoundryHost()
    {
        var (r, http) = await Run(new Options(Endpoint1: "https://evil.example.com/api/projects/p1"));
        Assert.DoesNotContain(http.Calls, c => c.Contains("evil.example.com"));
        Assert.Equal("No Foundry project endpoint is available.", r.Accounts[0].Projects[0].AgentsError);
    }

    [Fact]
    public async Task NoValidSubscriptionsReturnsAWarningWithoutCallingAzure()
    {
        var http = Handler(new Options());
        var result = await new FoundryDiscovery(new ArmClient(new FakeCredential(), new HttpClient(http)))
            .DiscoverAsync(new[] { "nope" }, true, CancellationToken.None);
        Assert.Empty(http.Calls);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task ThrottledRequestsAreRetriedThenSucceed()
    {
        var attempts = 0;
        var http = new FakeHttp((_, _) => ++attempts == 1 ? (HttpStatusCode.ServiceUnavailable, "{}") : (HttpStatusCode.OK, """{"data":[{"id":"x"}]}"""));
        var rows = await new ArmClient(new FakeCredential(), new HttpClient(http)).GraphAsync("resources", new[] { Sub }, CancellationToken.None);
        Assert.Equal(2, attempts);
        Assert.Single(rows);
    }
}
