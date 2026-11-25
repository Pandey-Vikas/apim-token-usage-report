using System.Net;
using System.Text;
using Azure.Core;

namespace TokenUsageReport.Tests;

internal sealed class FakeCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("test-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

/// <summary>Routes every HTTP call to a delegate and records "METHOD url" so tests can assert what was (not) called.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
{
    public List<string> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Calls) Calls.Add($"{request.Method} {request.RequestUri}");
        var (status, text) = respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
