using TokenUsageReport;
using Xunit;

namespace TokenUsageReport.Tests;

public sealed class WebAssetsTests
{
    [Fact]
    public void EveryDashboardFileIsEmbeddedAndNonEmpty()
    {
        foreach (var name in WebAssets.Names)
            Assert.False(string.IsNullOrWhiteSpace(WebAssets.ReadText(name)), $"{name} is empty");
    }

    [Fact]
    public void TheIndexPageIsSwitchedToHostedModeBeforeItsScriptsLoad()
    {
        var html = WebAssets.ReadText("index.html");
        Assert.DoesNotContain(WebAssets.HostedMarker, html);
        var flag = html.IndexOf(WebAssets.HostedScript, StringComparison.Ordinal);
        Assert.True(flag >= 0, "the hosted flag is injected");
        Assert.True(flag < html.IndexOf("wizard.js", StringComparison.Ordinal), "the flag precedes wizard.js");
        Assert.True(flag < html.IndexOf("dashboard.js", StringComparison.Ordinal), "the flag precedes dashboard.js");
    }

    [Fact]
    public void TheDashboardPageContainsNoSecretsOrData()
    {
        foreach (var name in WebAssets.Names)
        {
            var text = WebAssets.ReadText(name);
            Assert.DoesNotContain("REPORTS_API_KEY", text.Replace("X-Api-Key", ""), StringComparison.Ordinal);
            Assert.DoesNotContain("InstrumentationKey", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("styles.css", "text/css")]
    [InlineData("wizard.js", "text/javascript")]
    [InlineData("dashboard.js", "text/javascript")]
    public void ContentTypesAreCorrect(string name, string expected) => Assert.StartsWith(expected, WebAssets.ContentType(name));
}
