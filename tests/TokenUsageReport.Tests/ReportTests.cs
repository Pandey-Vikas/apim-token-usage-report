using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Monitor.Query;
using Kusto.Language;
using Kusto.Language.Symbols;
using TokenUsageReport.Models;
using TokenUsageReport.Services;
using Xunit;

namespace TokenUsageReport.Tests;

public sealed class ReportTests
{
    private const string ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/test/providers/microsoft.insights/components/test";
    private static readonly string[] Columns = [.. AppInsightsClient.RequiredColumns, "AgentId", "AgentName", "ReportedCost", "CostCurrency"];

    [Theory]
    [InlineData("apim-token-metrics")]
    [InlineData("foundry-genai")]
    [InlineData("genai-token-metric")]
    public void BundledQueriesHaveValidKustoSemantics(string name)
    {
        var schema = "(timestamp:datetime, customDimensions:dynamic, customMeasurements:dynamic, id:string, itemId:string, operation_Id:string, operation_ParentId:string, target:string, name:string, valueSum:real, valueCount:long)";
        var tables = new[] { "dependencies", "requests", "traces", "customEvents", "customMetrics" }
            .Select(t => new TableSymbol(t, schema));
        var globals = GlobalState.Default.WithDatabase(new DatabaseSymbol("Telemetry", tables));
        var code = KustoCode.ParseAndAnalyze(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Queries", name + ".kql")), globals);
        var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Start}: {e.Message}")));
        var result = Assert.IsType<TableSymbol>(code.ResultType);
        Assert.All(AppInsightsClient.RequiredColumns, required => Assert.Contains(result.Columns, column => column.Name == required));
        if (name != "apim-token-metrics")
        {
            Assert.Contains(result.Columns, column => column.Name == "AgentId");
            Assert.Contains(result.Columns, column => column.Name == "AgentName");
        }
        if (name == "foundry-genai")
        {
            Assert.Contains(result.Columns, column => column.Name == "ResourceId");
            Assert.Contains(result.Columns, column => column.Name == "PortalUrl");
        }
    }

    [Fact]
    public async Task DefaultFallsBackToFoundryAndPreservesAgentsAndCost()
    {
        using var handler = new QueryHandler((call, _) => call == 1 ? Table([]) : Table([
            ["", "gpt-4o", "example.openai.azure.com", 120L, 30L, 10L, 150L, 1L, "agent-1", "Support", "0.0123", "USD"],
            ["", "gpt-4o", "example.openai.azure.com", 200L, 50L, 0L, 250L, 1L, "agent-2", "Sales", null, ""]
        ]));
        var result = await Query(handler);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("foundry-genai", result.Source);
        Assert.Equal(new[] { "Support", "Sales" }, result.Rows.Select(r => r.AgentName));
        Assert.Equal(0.0123m, result.Rows[0].ReportedCost);
        Assert.Null(result.Rows[1].ReportedCost);
        Assert.Equal(150, result.Rows[0].TotalTokens);
        Assert.Contains("not automatically stored", result.Warning);
    }

    [Fact]
    public async Task ApimDataDoesNotAlsoReadFoundry()
    {
        using var handler = new QueryHandler((_, _) => Table([["sub", "model", "host", 10L, 5L, 0L, 15L, 1L, "", "", null, ""]]));
        var result = await Query(handler);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("apim-token-metrics", result.Source);
        Assert.Equal(15, Assert.Single(result.Rows).TotalTokens);
    }

    [Fact]
    public async Task EmittedZeroCostIsDistinctFromMissingCost()
    {
        using var handler = new QueryHandler((_, _) => Table([
            ["", "model", "host", 10L, 5L, 0L, 15L, 1L, "agent-1", "Support", "0", ""]
        ]));
        var result = await Query(handler, "custom query");
        Assert.Equal(0m, Assert.Single(result.Rows).ReportedCost);
        Assert.Contains("no emitted currency", result.Warning);
    }

    [Fact]
    public async Task EmptyDefaultTriesAllSourcesAndReturnsActionableWarning()
    {
        using var handler = new QueryHandler((_, _) => Table([]));
        var result = await Query(handler);
        Assert.Equal(3, handler.Calls);
        Assert.Empty(result.Rows);
        Assert.Equal("automatic", result.Source);
        Assert.Contains("Use Diagnose", result.Warning);
        Assert.Contains(ResourceId, result.Warning);
    }

    [Fact]
    public async Task CustomQueryNeverSilentlyChangesSource()
    {
        using var handler = new QueryHandler((_, _) => Table([]));
        var result = await Query(handler, "dependencies | take 0");
        Assert.Equal(1, handler.Calls);
        Assert.Equal("custom", result.Source);
    }

    [Fact]
    public async Task MissingRequiredColumnsFailsEvenWhenThereAreNoRows()
    {
        using var handler = new QueryHandler((_, _) => """{"tables":[{"name":"PrimaryResult","columns":[{"name":"TotalTokens","type":"long"}],"rows":[]}]}""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Query(handler));
        Assert.Contains("missing required columns", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PartialQueryFailureIsNotAnEmptySuccess()
    {
        using var handler = new QueryHandler((_, _) => Table([]).TrimEnd('}') + ""","error":{"code":"PartialError","message":"Query incomplete","details":[]}}""");
        await Assert.ThrowsAnyAsync<Exception>(() => Query(handler));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task QueryErrorsAreNotMaskedByFallback()
    {
        using var handler = new QueryHandler((_, _) => """{"error":{"code":"BadArgumentError","message":"Invalid query"}}""", HttpStatusCode.BadRequest);
        await Assert.ThrowsAnyAsync<Exception>(() => Query(handler));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void CsvAppendsAgentAndNullableCostWithoutChangingExistingColumns()
    {
        var csv = CsvWriter.Build([
            new EnrichedRow { AgentId = "agent-1", AgentName = "Support, \"tier 2\"", ReportedCost = 0.0012m, CostCurrency = "USD", TotalTokens = 15,
                ResourceId = FoundryProject, PortalUrl = PortalLinks.ForResource(FoundryProject) },
            new EnrichedRow { AgentId = "agent-2", AgentName = "Sales" }
        ]);
        var lines = csv.Split(Environment.NewLine);
        Assert.StartsWith("SubscriptionId,DeploymentName,Backend,", lines[0]);
        Assert.EndsWith(",Calls,AgentId,AgentName,ReportedCost,CostCurrency,ResourceId,PortalUrl", lines[0]);
        Assert.Contains("\"Support, \"\"tier 2\"\"\"", lines[1]);
        Assert.EndsWith($",0.0012,USD,{FoundryProject},https://portal.azure.com/#resource{FoundryProject}/overview", lines[1]);
        Assert.EndsWith(",agent-2,Sales,,,,", lines[2]);
    }

    private const string FoundryProject = "/subscriptions/a3b144fd-39ee-4729-b816-3ed42fc8f31f/resourceGroups/aifoundrydemo/providers/Microsoft.CognitiveServices/accounts/aifoundrydemoproject/projects/firstProject";
    private const string Account = "/subscriptions/a3b144fd-39ee-4729-b816-3ed42fc8f31f/resourceGroups/rg-ai/providers/Microsoft.CognitiveServices/accounts/myaccount";

    [Fact]
    public async Task QueryResourceIdIsReadAndOptionalWhenAbsent()
    {
        string[] columns = [.. Columns, "ResourceId"];
        object?[][] rows = [["", "gpt-4o", "host", 1L, 1L, 0L, 2L, 1L, "a", "Agent", null, "", FoundryProject]];
        var json = JsonSerializer.Serialize(new { tables = new[] { new { name = "PrimaryResult",
            columns = columns.Select(name => new { name, type = name == "ReportedCost" ? "decimal" :
                name.EndsWith("Tokens") || name == "Calls" ? "long" : "string" }), rows } } });
        using var handler = new QueryHandler((_, _) => json);
        var result = await Query(handler, "custom query");
        Assert.Equal(FoundryProject, Assert.Single(result.Rows).ResourceId);

        using var without = new QueryHandler((_, _) => Table([["", "m", "h", 1L, 1L, 0L, 2L, 1L, "", "", null, ""]]));
        Assert.Equal("", Assert.Single((await Query(without, "custom query")).Rows).ResourceId);
    }

    [Theory]
    [InlineData(FoundryProject, FoundryProject)]
    [InlineData("", Account)]
    [InlineData("javascript:alert(1)", Account)]
    [InlineData("/subscriptions/a3b144fd-39ee-4729-b816-3ed42fc8f31f/resourceGroups/rg/providers/x/y?q=\"><script>", Account)]
    public void TelemetryResourceWinsAndUnsafeValuesFallBackToInventory(string telemetry, string expectedResource)
    {
        var (resourceId, url) = PortalLinks.Choose(telemetry, Account);
        Assert.Equal(expectedResource, resourceId);
        Assert.Equal($"https://portal.azure.com/#resource{expectedResource}/overview", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-arm-id")]
    [InlineData("/subscriptions/not-a-guid/resourceGroups/rg/providers/x/y")]
    public void NoLinkWhenNeitherSourceIsAValidResource(string? invalid)
    {
        Assert.Equal(("", ""), PortalLinks.Choose(invalid, invalid));
        Assert.Equal("", PortalLinks.ForResource(invalid));
    }

    private static string Table(object?[][] rows) => JsonSerializer.Serialize(new
    {
        tables = new[] { new { name = "PrimaryResult",
            columns = Columns.Select(name => new { name, type = name == "ReportedCost" ? "decimal" :
                name.EndsWith("Tokens") || name == "Calls" ? "long" : "string" }),
            rows } }
    });

    private static async Task<TokenQueryResult> Query(QueryHandler handler, string? customQuery = null)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        var options = new LogsQueryClientOptions { Transport = new HttpClientTransport(http), Retry = { MaxRetries = 0 } };
        var client = new AppInsightsClient(ResourceId, new LogsQueryClient(new TestCredential(), options), customQuery);
        return await client.QueryAsync(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class QueryHandler(Func<int, string, string> response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response(++Calls, body), Encoding.UTF8, "application/json")
            };
        }
    }
}
