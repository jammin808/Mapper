using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PlayMap.Core.Bus;
using PlayMap.Net.Http;

namespace PlayMap.Net.Tests;

/// <summary>A part's pages under its own path of the web port: it admits its requests before the token gate — open, refused with its
/// words, or deferred to the gate — and it never serves under the node's own paths (an open module there would open them).</summary>
public sealed class WebModuleTests
{
    /// <summary>The phone's page as a module: an open page, a refused one, and one behind the node's token.</summary>
    private sealed class ScanPage(string prefix = "/scan") : IWebModule
    {
        public string Name => "phone page";

        public string PathPrefix => prefix;

        public WebAdmission Admit(HttpContext context, NodeSettings settings, out string words)
        {
            var path = context.Request.Path.Value ?? "";
            words = path == "/scan/expired" ? "this phone's link has expired — scan the new code" : "";
            return path switch
            {
                "/scan/page" => WebAdmission.Open,
                "/scan/expired" => WebAdmission.Refuse,
                _ => WebAdmission.Defer,
            };
        }

        public void Map(IEndpointRouteBuilder routes)
        {
            routes.MapGet("/scan/page", c => c.Response.WriteAsync("the page"));
            routes.MapGet("/scan/token", c => c.Response.WriteAsync("behind the token"));
        }
    }

    [Fact]
    public async Task AModuleAdmitsItsOwnRequestsBeforeTheTokenGateAndDefersTheRest()
    {
        var endpoint = new FakeEndpoint();
        await using var host = await HttpHostTests.StartAsync(endpoint, TestSettings.Make(trustLoopback: false),
            new HttpHostOptions { Modules = [new ScanPage()] });
        using var anonymous = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + host.Port), Timeout = TimeSpan.FromSeconds(30) };

        Assert.Equal("the page", await anonymous.GetStringAsync("/scan/page"));

        using (var expired = await anonymous.GetAsync("/scan/expired"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
            using var body = JsonDocument.Parse(await expired.Content.ReadAsStringAsync());
            Assert.Equal("this phone's link has expired — scan the new code", body.RootElement.GetProperty("error").GetString());
        }

        // Deferred: the node's gate decides, as for its own routes.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/scan/token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/state")).StatusCode);
        using var paired = new HttpClient { BaseAddress = anonymous.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        paired.DefaultRequestHeaders.Add("X-PlayMap-Token", TestSettings.Token);
        Assert.Equal("behind the token", await paired.GetStringAsync("/scan/token"));
    }

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/scan")]
    [InlineData("/ws")]
    [InlineData("/MCP")]
    [InlineData("/tls")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("scan")]
    public void AModuleNeverServesUnderTheNodesOwnPaths(string prefix)
    {
        Assert.NotNull(HttpHost.WhyNotAModulePath(prefix));
        var refused = Assert.Throws<ArgumentException>(() =>
            new HttpHost(TestSettings.Make(), new FakeEndpoint(), TestSettings.Wall(), new HttpHostOptions { Modules = [new ScanPage(prefix)] }));
        Assert.Contains("phone page", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/scan")]
    [InlineData("/scan/")]
    [InlineData("/apiary")]
    public void APathOfItsOwnIsTaken(string prefix) => Assert.Null(HttpHost.WhyNotAModulePath(prefix));
}
