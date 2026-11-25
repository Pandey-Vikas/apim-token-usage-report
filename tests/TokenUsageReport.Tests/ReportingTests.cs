using System.Globalization;
using System.Net;
using Azure.Core.Pipeline;
using Azure.Monitor.Query;
using Kusto.Language;
using Kusto.Language.Symbols;
using TokenUsageReport.Reporting;
using Xunit;

namespace TokenUsageReport.Tests;

public sealed class PriceBookTests
{
    private static RetailMeter M(string name, decimal price, string unit = "1K", string region = "eastus2") => new(name, price, unit, region);

    private static readonly RetailMeter[] Mini =
    {
        M("gpt-4o-mini-0718-Inp-glbl Tokens", 0.00015m), M("gpt-4o-mini-0718-Outp-glbl Tokens", 0.0006m),
        M("gpt-4o-mini-0718-Inp-regnl Tokens", 0.000165m), M("gpt-4o-mini-0718-Outp-regnl Tokens", 0.00066m),
        M("gpt-4o-mini-0718-Batch-Inp-glbl Tokens", 0.000075m), // batch pricing is a different product
        M("gpt-4o-mini-transcribe-txt-out-glbl Tokens", 0.005m),
    };

    [Fact]
    public void SelectsGlobalAndRegionalMeterPairsAndConvertsToPerMillion()
    {
        var global = PriceBook.Select(Mini, "gpt-4o-mini", "2024-07-18", "glbl")!;
        Assert.Equal((0.15m, 0.6m), (global.InputPer1M, global.OutputPer1M));
        Assert.Null(global.CachedInputPer1M);
        Assert.Contains("gpt-4o-mini-0718", global.Source);
        var regional = PriceBook.Select(Mini, "gpt-4o-mini", "2024-07-18", "regnl")!;
        Assert.Equal((0.165m, 0.66m), (regional.InputPer1M, regional.OutputPer1M));
        Assert.Null(PriceBook.Select(Mini, "gpt-4o-mini", "2024-07-18", "dz"));
    }

    [Fact]
    public void BaseModelNeverMatchesALongerModelName()
    {
        // "gpt-4o" must not pick up the gpt-4o-mini meters.
        Assert.Null(PriceBook.Select(Mini, "gpt-4o", "2024-07-18", "glbl"));
    }

    [Fact]
    public void EmbeddingModelsPriceInputOnly()
    {
        var p = PriceBook.Select(new[] { M("text-embedding-3-small-glbl Tokens", 0.00002m) }, "text-embedding-3-small", "1", "glbl")!;
        Assert.Equal(0.02m, p.InputPer1M);
        Assert.Equal(0m, p.OutputPer1M);
    }

    [Fact]
    public void CachedRateIsUsedWhenAMeterExists()
    {
        var meters = Mini.Concat(new[] { M("gpt-4o-mini-0718-cchd-inp-glbl Tokens", 0.000075m) });
        var p = PriceBook.Select(meters, "gpt-4o-mini", "2024-07-18", "glbl")!;
        Assert.Equal(0.075m, p.CachedInputPer1M);
        // 600 uncached * 0.15 + 400 cached * 0.075 + 100 out * 0.6 = 90 + 30 + 60 = 180 per million
        Assert.Equal(0.00018m, PriceBook.Cost(p, 1000, 400, 100));
        // Without a cached rate every prompt token is billed at the input rate.
        Assert.Equal(0.00021m, PriceBook.Cost(new ModelPrice(0.15m, null, 0.6m, ""), 1000, 400, 100));
    }

    [Fact]
    public void UnlistedVersionIsOnlyPricedWhenEveryListedVersionAgrees()
    {
        var same = new[]
        {
            M("gpt-4o-0513-Inp-glbl Tokens", 0.0025m), M("gpt-4o-0513-Outp-glbl Tokens", 0.01m),
            M("gpt-4o-0806-Inp-glbl Tokens", 0.0025m), M("gpt-4o-0806-Outp-glbl Tokens", 0.01m),
        };
        var p = PriceBook.Select(same, "gpt-4o", "2024-11-20", "glbl")!;
        Assert.Equal(2.5m, p.InputPer1M);
        Assert.Contains("identical across listed versions", p.Source);

        var differ = same.Select(m => m.MeterName.Contains("0806-Inp") ? m with { RetailPrice = 0.003m } : m).ToArray();
        Assert.Null(PriceBook.Select(differ, "gpt-4o", "2024-11-20", "glbl"));
        // One unrelated version is not evidence for the requested one.
        Assert.Null(PriceBook.Select(same.Skip(2), "gpt-4o", "2024-11-20", "glbl"));
        // An exact version match always wins.
        Assert.Equal(3m, PriceBook.Select(differ, "gpt-4o", "2024-08-06", "glbl")!.InputPer1M);
    }

    [Fact]
    public void FineTunedHostingAndUnknownMetersAreNeverUsed()
    {
        var meters = new[] { M("gpt-4.1-mini-ft input global Tokens", 0.0008m), M("gpt-4.1-mini-ft output global Tokens", 0.0032m), M("gpt-4.1-mini-ft hosting global Unit", 1.7m, "1 Hour") };
        Assert.Null(PriceBook.Select(meters, "gpt-4.1-mini", "2025-04-14", "glbl"));
        Assert.Null(PriceBook.Select(Mini.Select(m => m with { UnitOfMeasure = "1 Hour" }), "gpt-4o-mini", "2024-07-18", "glbl"));
    }

    [Theory]
    [InlineData("GlobalStandard", "glbl")]
    [InlineData("DataZoneStandard", "dz")]
    [InlineData("Standard", "regnl")]
    [InlineData("GlobalBatch", "")]
    [InlineData("ProvisionedManaged", "")]
    [InlineData(null, "")]
    public void OnlyStandardSkusGetAPriceKind(string? sku, string expected) => Assert.Equal(expected, PriceBook.SkuKindOf(sku));

    [Theory]
    [InlineData("gpt-4o-mini-2024-07-18", "gpt-4o-mini", "2024-07-18")]
    [InlineData("gpt-5", "gpt-5", "")]
    [InlineData("text-embedding-3-small", "text-embedding-3-small", "")]
    public void SplitsModelNamesIntoBaseAndVersion(string model, string expectedBase, string expectedVersion) =>
        Assert.Equal((expectedBase, expectedVersion), PriceBook.SplitModel(model));

    [Fact]
    public async Task OverridesWinCacheResultsAndRejectUnsafeNames()
    {
        var calls = 0;
        var http = new FakeHttp((_, _) => { calls++; return (HttpStatusCode.OK, """{"Items":[{"meterName":"gpt-4o-mini-0718-Inp-glbl Tokens","retailPrice":0.00015,"unitOfMeasure":"1K"},{"meterName":"gpt-4o-mini-0718-Outp-glbl Tokens","retailPrice":0.0006,"unitOfMeasure":"1K"}]}"""); });
        var book = new PriceBook(new HttpClient(http), PriceBook.ParseOverrides("""{"my-deployment":{"input":1,"output":2}}"""));

        Assert.Equal(1m, (await book.GetAsync("my-deployment", "gpt-4o-mini", "2024-07-18", "glbl", null, default))!.InputPer1M);
        Assert.Equal(0, calls);

        Assert.NotNull(await book.GetAsync("d", "gpt-4o-mini", "2024-07-18", "glbl", "eastus2", default));
        Assert.NotNull(await book.GetAsync("d", "gpt-4o-mini", "2024-07-18", "glbl", "eastus2", default));
        Assert.Equal(1, calls);

        Assert.Null(await book.GetAsync("d", "gpt'; drop", "", "glbl", null, default));
        Assert.Null(await book.GetAsync("d", "gpt-4o-mini", "", "", null, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TransientPriceFailureIsNotRemembered()
    {
        var fail = true;
        var http = new FakeHttp((_, _) => fail ? (HttpStatusCode.InternalServerError, "{}")
            : (HttpStatusCode.OK, """{"Items":[{"meterName":"gpt-4o-mini-0718-Inp-glbl Tokens","retailPrice":0.00015,"unitOfMeasure":"1K"},{"meterName":"gpt-4o-mini-0718-Outp-glbl Tokens","retailPrice":0.0006,"unitOfMeasure":"1K"}]}"""));
        var book = new PriceBook(new HttpClient(http));
        Assert.Null(await book.GetAsync("d", "gpt-4o-mini", "2024-07-18", "glbl", null, default));
        fail = false;
        Assert.NotNull(await book.GetAsync("d", "gpt-4o-mini", "2024-07-18", "glbl", null, default));
    }

    [Fact]
    public void MalformedOverridesAreRejectedWithAClearMessage()
    {
        Assert.Throws<FormatException>(() => PriceBook.ParseOverrides("{not json"));
        var ex = Assert.Throws<FormatException>(() => PriceBook.ParseOverrides("""{"gpt-4o":{"input":1}}"""));
        Assert.Contains("input", ex.Message);
        Assert.Empty(PriceBook.ParseOverrides(null));
    }
}

public sealed class UsageReportServiceTests
{
    private const string Sub1 = "11111111-1111-1111-1111-111111111111";
    private const string Sub2 = "22222222-2222-2222-2222-222222222222";
    private static readonly string AcctId = $"/subscriptions/{Sub1}/resourceGroups/rg1/providers/Microsoft.CognitiveServices/accounts/acct";
    private static string Ai(string name) => $"/subscriptions/{Sub1}/resourceGroups/obs/providers/microsoft.insights/components/{name}";

    private const string RetailJson = """{"Items":[{"meterName":"gpt-4o-mini-0718-Inp-glbl Tokens","retailPrice":0.00015,"unitOfMeasure":"1K","armRegionName":"eastus2"},{"meterName":"gpt-4o-mini-0718-Outp-glbl Tokens","retailPrice":0.0006,"unitOfMeasure":"1K","armRegionName":"eastus2"}],"NextPageLink":null}""";

    private sealed class FakeDiscovery(DiscoveryResult result) : IFoundryDiscovery
    {
        public Task<DiscoveryResult> DiscoverAsync(IReadOnlyCollection<string> s, bool agents, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class FakeUsage(Func<string, List<UsageRow>> rows) : IUsageSource
    {
        public Task<List<UsageRow>> QueryAsync(string id, DateTimeOffset s, DateTimeOffset e, CancellationToken ct) => Task.FromResult(rows(id));
    }

    private static FoundryProject Proj(string name, params string[] ai) => new() { Id = $"{AcctId}/projects/{name}", Name = name, AppInsightsIds = ai.ToList() };

    private static DiscoveryResult Inventory(FoundryProject[] projects, params string[] insights)
    {
        var account = new FoundryAccount { Id = AcctId, Name = "acct", SubscriptionId = Sub1, ResourceGroup = "rg1", Location = "eastus2" };
        account.Deployments.Add(new DeploymentInfo("gpt-4o-mini", "gpt-4o-mini", "2024-07-18", "GlobalStandard", "50"));
        account.Projects.AddRange(projects);
        var result = new DiscoveryResult { TenantId = "tenant-1" };
        result.Subscriptions.Add(new SubscriptionInfo(Sub1, "Prod", "tenant-1"));
        result.Accounts.Add(account);
        result.AppInsights.AddRange(insights.Select(n => new AppInsightsInfo(Ai(n), n, Sub1, "obs", "eastus2")));
        return result;
    }

    private static UsageRow Row(string projectId, string agent = "Alpha", long prompt = 1000, long completion = 100, long cached = 0, string day = "2026-11-24", decimal? reported = null, string currency = "") =>
        new(DateTime.Parse(day, CultureInfo.InvariantCulture), projectId, agent.ToLowerInvariant(), agent, "gpt-4o-mini-2024-07-18", "gpt-4o-mini", "host",
            prompt, completion, cached, 2, 0, 500, reported, currency, null, null);

    private static PriceBook Retail(bool fail = false, IReadOnlyDictionary<string, ModelPrice>? overrides = null) =>
        new(new HttpClient(new FakeHttp((_, _) => fail ? (HttpStatusCode.InternalServerError, "{}") : (HttpStatusCode.OK, RetailJson))), overrides);

    private static Task<UsageReport> Run(DiscoveryResult inventory, Func<string, List<UsageRow>> rows, PriceBook? prices = null, params string[] subs) =>
        new UsageReportService(new FakeDiscovery(inventory), new FakeUsage(rows), prices ?? Retail())
            .RunAsync(new ReportRequest(subs.Length == 0 ? new[] { Sub1 } : subs, DateTimeOffset.Parse("2026-11-01Z"), DateTimeOffset.Parse("2026-11-25Z"), "tenant-1", "Contoso"), default);

    [Fact]
    public async Task AttributesUsageToSubscriptionGroupProjectAndPricesIt()
    {
        var p1 = Proj("p1", Ai("ai1"));
        var report = await Run(Inventory(new[] { p1 }, "ai1"), _ => new() { Row(p1.Id) });

        Assert.Equal(("tenant-1", "Contoso"), (report.Tenant.Id, report.Tenant.Name));
        var scope = Assert.Single(report.Scopes);
        Assert.Equal(("Prod", "rg1", "acct", "p1", "ai1"), (scope.SubscriptionName, scope.ResourceGroup, scope.AccountName, scope.ProjectName, scope.AppInsightsName));
        Assert.StartsWith("https://portal.azure.com/#resource/subscriptions/", scope.PortalUrl);
        var fact = Assert.Single(report.Facts);
        Assert.Equal((1100L, "GlobalStandard", "list"), (fact.TotalTokens, fact.Sku, fact.CostBasis));
        Assert.Equal(0.00021m, fact.EstimatedCost); // (1000 * 0.15 + 100 * 0.60) / 1M
        Assert.Equal(0.15m, Assert.Single(report.Prices).InputPer1M);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task DeploymentIsMatchedByModelWhenTelemetryCarriesOnlyTheModelName()
    {
        // Real Foundry telemetry reports "gpt-4o-mini-2024-07-18" as the model while the deployment is named "gpt-4o-mini".
        var p1 = Proj("p1", Ai("ai1"));
        var row = Row(p1.Id) with { Deployment = "gpt-4o-mini-2024-07-18" };
        var report = await Run(Inventory(new[] { p1 }, "ai1"), _ => new() { row });
        Assert.Equal(("GlobalStandard", "list"), (report.Facts.Single().Sku, report.Facts.Single().CostBasis));
        Assert.Equal(0.00021m, report.Facts.Single().EstimatedCost);
    }

    [Fact]
    public async Task AmbiguousSkuForTheSameModelGivesNoEstimateInsteadOfAGuess()
    {
        var p1 = Proj("p1", Ai("ai1"));
        var inventory = Inventory(new[] { p1 }, "ai1");
        inventory.Accounts[0].Deployments.Add(new DeploymentInfo("mini-regional", "gpt-4o-mini", "2024-07-18", "Standard", "10"));
        var row = Row(p1.Id) with { Deployment = "gpt-4o-mini-2024-07-18" };
        var report = await Run(inventory, _ => new() { row });
        Assert.Null(report.Facts.Single().EstimatedCost);
        Assert.Equal("", report.Facts.Single().Sku);
    }

    [Fact]
    public async Task ProjectNamesComeFromTheLastSegmentOfTheArmName()
    {
        var http = new FakeHttp((req, body) =>
        {
            if (!req.RequestUri!.ToString().Contains("ResourceGraph")) return (HttpStatusCode.OK, """{"value":[]}""");
            if (body.Contains("accounts/projects")) return (HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new { data = new[] { new { id = "/subscriptions/a3b144fd-39ee-4729-b816-3ed42fc8f31f/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/acct/projects/proj", name = "acct/proj", apiEndpoint = "" } } }));
            if (body.Contains("AIServices")) return (HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new { data = new[] { new { id = "/subscriptions/a3b144fd-39ee-4729-b816-3ed42fc8f31f/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/acct", name = "acct", subscriptionId = "a3b144fd-39ee-4729-b816-3ed42fc8f31f", resourceGroup = "rg", location = "eastus" } } }));
            return (HttpStatusCode.OK, """{"data":[]}""");
        });
        var result = await new FoundryDiscovery(new ArmClient(new FakeCredential(), new HttpClient(http)))
            .DiscoverAsync(new[] { "a3b144fd-39ee-4729-b816-3ed42fc8f31f" }, false, default);
        Assert.Equal("proj", result.Accounts.Single().Projects.Single().Name);
    }

    [Fact]
    public async Task ReportedCostIsKeptAndWinsTheBasisButEstimateIsStillShown()
    {
        var p1 = Proj("p1", Ai("ai1"));
        var report = await Run(Inventory(new[] { p1 }, "ai1"), _ => new() { Row(p1.Id, reported: 0.5m, currency: "USD") });
        var fact = report.Facts.Single();
        Assert.Equal((0.5m, 0.00021m, "reported"), (fact.ReportedCost, fact.EstimatedCost, fact.CostBasis));
    }

    [Fact]
    public async Task CustomPriceOverridesUseTheCachedRateAndAreLabelledCustom()
    {
        var p1 = Proj("p1", Ai("ai1"));
        var prices = Retail(overrides: PriceBook.ParseOverrides("""{"gpt-4o-mini":{"input":1,"cachedInput":0.5,"output":2}}"""));
        var report = await Run(Inventory(new[] { p1 }, "ai1"), _ => new() { Row(p1.Id, cached: 400) }, prices);
        var fact = report.Facts.Single();
        Assert.Equal(0.001m, fact.EstimatedCost); // 600 * 1 + 400 * 0.5 + 100 * 2 = 1000 per million
        Assert.Equal("custom", fact.CostBasis);
    }

    [Fact]
    public async Task UnavailablePricingLeavesTokensIntactAndCostBlank()
    {
        var p1 = Proj("p1", Ai("ai1"));
        var report = await Run(Inventory(new[] { p1 }, "ai1"), _ => new() { Row(p1.Id) }, Retail(fail: true));
        var fact = report.Facts.Single();
        Assert.Equal(1100L, fact.TotalTokens);
        Assert.Null(fact.EstimatedCost);
        Assert.Equal("", fact.CostBasis);
        Assert.Empty(report.Prices);
    }

    [Fact]
    public async Task SharedInsightsKeepsEachProjectSeparateByTelemetryProjectId()
    {
        var p1 = Proj("p1", Ai("shared"));
        var p2 = Proj("p2", Ai("shared"));
        var report = await Run(Inventory(new[] { p1, p2 }, "shared"), _ => new() { Row(p1.Id, "Alpha"), Row(p2.Id, "Beta") });
        Assert.Equal(new[] { "p1", "p2" }, report.Scopes.Select(s => s.ProjectName).OrderBy(n => n));
        Assert.Equal(2, report.Facts.Select(f => f.Scope).Distinct().Count());
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task RowsWithoutAProjectIdAreAttributedOnlyWhenTheLinkIsUnambiguous()
    {
        var single = Proj("only", Ai("ai1"));
        var one = await Run(Inventory(new[] { single }, "ai1"), _ => new() { Row("") });
        Assert.Equal("only", one.Scopes.Single().ProjectName);

        var a = Proj("a", Ai("shared"));
        var b = Proj("b", Ai("shared"));
        var many = await Run(Inventory(new[] { a, b }, "shared"), _ => new() { Row("") });
        Assert.Equal("(unattributed)", many.Scopes.Single().ProjectName);
        Assert.Contains(many.Warnings, w => w.Scope == "Attribution" && w.Message.Contains("(unattributed)"));
    }

    [Fact]
    public async Task ProjectsOutsideTheSelectedSubscriptionsAreLeftOutWithAWarning()
    {
        var p1 = Proj("p1", Ai("shared"));
        var foreign = $"/subscriptions/{Sub2}/resourceGroups/x/providers/Microsoft.CognitiveServices/accounts/other/projects/theirs";
        var deleted = $"/subscriptions/{Sub1}/resourceGroups/rgx/providers/Microsoft.CognitiveServices/accounts/old/projects/gone";
        var report = await Run(Inventory(new[] { p1 }, "shared"), _ => new() { Row(p1.Id), Row(foreign, "Leak"), Row(deleted, "Legacy") });

        Assert.DoesNotContain(report.Facts, f => f.AgentName == "Leak");
        Assert.Contains(report.Warnings, w => w.Scope == "Scope" && w.Message.Contains("outside the selected subscriptions"));
        var gone = report.Scopes.Single(s => s.ProjectName == "gone"); // a deleted project inside the scope is still reported
        Assert.Equal(("Prod", "rgx", "old"), (gone.SubscriptionName, gone.ResourceGroup, gone.AccountName));
    }

    [Fact]
    public async Task AFailingInsightsBecomesAWarningAndTheOthersAreStillReported()
    {
        var p1 = Proj("p1", Ai("good"));
        var p2 = Proj("p2", Ai("bad"));
        var report = await Run(Inventory(new[] { p1, p2 }, "good", "bad"), id => id.EndsWith("/bad") ? throw new InvalidOperationException("403 forbidden\nsecond line") : new() { Row(p1.Id) });
        Assert.Single(report.Facts);
        var warning = Assert.Single(report.Warnings);
        Assert.Equal("bad", warning.Scope);
        Assert.Contains("403 forbidden", warning.Message);
        Assert.DoesNotContain("second line", warning.Message);
        Assert.Contains("Monitoring Reader", warning.Message);
    }

    [Fact]
    public async Task NonUsdReportedCostsWarnThatTotalsMixCurrencies()
    {
        var p1 = Proj("p1", Ai("ai1"));
        var report = await Run(Inventory(new[] { p1 }, "ai1"), _ => new() { Row(p1.Id, reported: 1m, currency: "EUR") });
        Assert.Contains(report.Warnings, w => w.Scope == "Cost");
    }
}

public sealed class UsageCsvTests
{
    private static UsageFact Fact(string day, string agent = "Alpha", long tokens = 100, decimal? estimated = 0.01m, decimal? reported = null) => new()
    {
        Day = day, Scope = 0, AgentName = agent, AgentId = agent.ToLowerInvariant(), Model = "gpt-4o-mini", Deployment = "gpt-4o-mini",
        PromptTokens = tokens, TotalTokens = tokens, Calls = 2, DurationMsSum = 1000, EstimatedCost = estimated, ReportedCost = reported,
        CostBasis = estimated is null ? "" : "list", FirstSeen = day + "T01:00:00Z", LastSeen = day + "T02:00:00Z",
    };

    private static UsageReport Report(params UsageFact[] facts)
    {
        var report = new UsageReport { Tenant = new TenantInfo("t", "Contoso") };
        report.Scopes.Add(new ReportScope { SubscriptionId = "s", SubscriptionName = "Prod", ResourceGroup = "rg", AccountName = "acct", ProjectName = "p1", AppInsightsName = "ai" });
        report.Facts.AddRange(facts);
        return report;
    }

    [Theory]
    [InlineData("2026-11-25", "day", "2026-11-25")]
    [InlineData("2026-11-25", "week", "2026-11-23")] // Wednesday -> Monday
    [InlineData("2026-11-29", "week", "2026-11-23")] // Sunday belongs to the week that started on Monday
    [InlineData("2026-11-30", "week", "2026-11-30")]
    [InlineData("2026-11-25", "month", "2026-11-01")]
    public void PeriodStartBucketsDaysIntoWeeksAndMonths(string day, string grain, string expected) =>
        Assert.Equal(expected, UsageCsv.PeriodStart(day, grain));

    [Fact]
    public void UnknownGranularityIsRejected() => Assert.Throws<ArgumentException>(() => UsageCsv.PeriodStart("2026-11-25", "year"));

    [Fact]
    public void WeeklyRowsSumTokensAndBlankTheCostWhenAnyDayIsUnpriced()
    {
        var csv = UsageCsv.Build(Report(Fact("2026-11-23"), Fact("2026-11-24", estimated: null), Fact("2026-11-30")), "week");
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + two weeks
        Assert.StartsWith("TenantId,Tenant,SubscriptionId,Subscription,ResourceGroup,FoundryResource,FoundryProject,AppInsights,Agent", lines[0]);
        var week1 = lines[1].Split(',');
        Assert.Equal("2026-11-23", week1[12]);
        Assert.Equal("200", week1[16]);   // TotalTokens summed across the two days
        Assert.Equal("4", week1[17]);     // Calls
        Assert.Equal("500", week1[19]);   // weighted latency: 2000 ms over 4 calls
        Assert.Equal("", week1[21]);      // one day has no price, so no partial (understated) sum
        Assert.Equal("mixed", week1[22]);
        var week2 = lines[2].Split(',');
        Assert.Equal(("2026-11-30", "0.01", "list"), (week2[12], week2[21], week2[22]));
    }

    [Fact]
    public void FormulaCharactersAreDefusedAndCommasQuoted()
    {
        var csv = UsageCsv.Build(Report(Fact("2026-11-24", agent: "=HYPERLINK(\"http://x\")"), Fact("2026-11-24", agent: "Sales, EMEA")), "day");
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", csv);
        Assert.Contains("\"Sales, EMEA\"", csv);
    }
}

public sealed class UsageQueryTests
{
    private static readonly (string Name, string Type)[] Columns =
    {
        ("Day", "datetime"), ("ProjectId", "string"), ("AgentId", "string"), ("AgentName", "string"), ("Model", "string"), ("Deployment", "string"),
        ("Backend", "string"), ("PromptTokens", "long"), ("CompletionTokens", "long"), ("CachedTokens", "long"), ("Calls", "long"), ("Errors", "long"),
        ("DurationMsSum", "real"), ("ReportedCost", "decimal"), ("CostCurrency", "string"), ("FirstSeen", "datetime"), ("LastSeen", "datetime"),
    };

    [Fact]
    public void BundledQueryIsValidKustoAndReturnsExactlyTheColumnsTheRunnerReads()
    {
        const string schema = "(timestamp:datetime, customDimensions:dynamic, customMeasurements:dynamic, id:string, itemId:string, operation_Id:string, operation_ParentId:string, target:string, name:string, duration:real, success:bool)";
        var tables = new[] { "dependencies", "requests", "traces", "customEvents" }.Select(t => new TableSymbol(t, schema));
        var globals = GlobalState.Default.WithDatabase(new DatabaseSymbol("Telemetry", tables));
        var code = KustoCode.ParseAndAnalyze(UsageQueryRunner.LoadBundledQuery(), globals);
        var errors = code.GetDiagnostics().Where(d => d.Severity == "Error").ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Select(e => $"{e.Start}: {e.Message}")));
        var result = Assert.IsType<TableSymbol>(code.ResultType);
        Assert.Equal(UsageQueryRunner.RequiredColumns.OrderBy(c => c), result.Columns.Select(c => c.Name).OrderBy(c => c));
    }

    private static string Table(object?[][] rows, string? omit = null) => System.Text.Json.JsonSerializer.Serialize(new
    {
        tables = new[] { new { name = "PrimaryResult", columns = Columns.Where(c => c.Name != omit).Select(c => new { name = c.Name, type = c.Type }), rows } },
    });

    private static Task<List<UsageRow>> Query(string json)
    {
        var options = new LogsQueryClientOptions { Transport = new HttpClientTransport(new HttpClient(new FakeHttp((_, _) => (HttpStatusCode.OK, json)))), Retry = { MaxRetries = 0 } };
        return new UsageQueryRunner(new LogsQueryClient(new FakeCredential(), options))
            .QueryAsync("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/r/providers/microsoft.insights/components/c", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default);
    }

    [Fact]
    public async Task ParsesUsageRowsIncludingCostAndTimestamps()
    {
        var rows = await Query(Table(new[]
        {
            new object?[] { "2026-11-24T00:00:00Z", "/subscriptions/s/resourceGroups/r/providers/p/accounts/a/projects/p1", "agent-1", "Alpha", "gpt-4o-mini-2024-07-18", "gpt-4o-mini", "host",
                1000, 100, 10, 2, 1, 2500.5, "0.0123", "USD", "2026-11-24T10:00:00Z", "2026-11-24T11:00:00Z" },
            new object?[] { "2026-11-25T00:00:00Z", "", "", "", "m", "d", "", 1, 1, 0, 1, 0, 0, null, "", null, null },
        }));
        Assert.Equal(2, rows.Count);
        Assert.Equal((new DateTime(2026, 11, 24), "Alpha", 1000L, 100L, 10L, 2L, 1L), (rows[0].Day, rows[0].AgentName, rows[0].PromptTokens, rows[0].CompletionTokens, rows[0].CachedTokens, rows[0].Calls, rows[0].Errors));
        Assert.Equal((2500.5, 0.0123m, "USD"), (rows[0].DurationMsSum, rows[0].ReportedCost, rows[0].CostCurrency));
        Assert.NotNull(rows[0].FirstSeen);
        Assert.Null(rows[1].ReportedCost);
        Assert.Null(rows[1].LastSeen);
    }

    [Fact]
    public async Task MissingColumnsAreAnErrorNotAnEmptyReport()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Query(Table(Array.Empty<object?[]>(), omit: "Errors")));
        Assert.Contains("Errors", ex.Message);
    }
}
