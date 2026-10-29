namespace Sankore.Shared.Infrastructure.Tests.Egress;

using System.Net;
using FluentAssertions;
using Sankore.Shared.Infrastructure.Egress;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The outbound HTTP client for URLs a tenant configured. The refusals below happen before any
/// packet leaves: either at save time (<see cref="SafeOutboundHttp.ValidateUrl"/>) or in the
/// connect callback, which is the one that holds when a name changes its answer.
/// </summary>
public sealed class SafeOutboundHttpTests
{
    [Theory]
    [InlineData("https://partner.example.com/hooks/sankore")]
    [InlineData("https://partner.example.com:8443/hooks")]
    [InlineData("https://8.8.8.8/x")]
    public void A_public_https_url_on_an_allowed_port_may_be_saved(string url)
        => SafeOutboundHttp.ValidateUrl(url).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas une url")]
    [InlineData("http://partner.example.com/hooks")]
    [InlineData("https://partner.example.com:6379/")]
    [InlineData("https://user:secret@partner.example.com/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/")]
    [InlineData("https://localhost/")]
    [InlineData("https://api.localhost/")]
    public void An_unusable_url_is_refused_with_a_reason(string? url)
        => SafeOutboundHttp.ValidateUrl(url).Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void The_port_list_is_the_platform_setting()
    {
        var options = new OutboundHttpOptions { AllowedPorts = [443, 9443] };

        SafeOutboundHttp.ValidateUrl("https://partner.example.com:9443/", options).Should().BeNull();
        SafeOutboundHttp.ValidateUrl("https://partner.example.com:8443/", options).Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://localhost/")]                // by NAME: every answer is loopback
    [InlineData("https://partner.example.com:6379/")] // refused before any resolution
    [InlineData("http://partner.example.com/")]
    public async Task The_handler_refuses_before_connecting(string url)
    {
        using var client = new HttpClient(SafeOutboundHttp.CreateHandler(new OutboundHttpOptions()));

        var act = () => client.GetAsync(url);

        // SocketsHttpHandler wraps what the connect callback throws; the code is the only detail
        // that crosses, deliberately the same for every refusal.
        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        thrown.Which.InnerException.Should().BeOfType<DomainException>()
            .Which.Message.Should().Be(SafeOutboundHttp.RefusedCode);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_refused()
    {
        var inner = new ScriptedHandler(Redirect(HttpStatusCode.Found, "https://169.254.169.254/latest"));
        using var client = new HttpClient(new SafeRedirectHandler { InnerHandler = inner });

        var act = () => client.GetAsync("https://partner.example.com/start");

        (await act.Should().ThrowAsync<DomainException>()).Which.Message.Should().Be(SafeOutboundHttp.RefusedCode);
        inner.Requests.Should().HaveCount(1, "the redirect target must never be requested");
    }

    [Fact]
    public async Task A_redirect_downgrading_to_http_is_refused()
    {
        var inner = new ScriptedHandler(Redirect(HttpStatusCode.MovedPermanently, "http://partner.example.com/start"));
        using var client = new HttpClient(new SafeRedirectHandler { InnerHandler = inner });

        var act = () => client.GetAsync("https://partner.example.com/start");

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task One_same_host_redirect_is_followed_and_no_second()
    {
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.Found, "/second"),
            Redirect(HttpStatusCode.Found, "/third"));
        using var client = new HttpClient(new SafeRedirectHandler { InnerHandler = inner });

        var response = await client.GetAsync("https://partner.example.com/first");

        response.StatusCode.Should().Be(HttpStatusCode.Found, "the second redirect is handed back, not followed");
        inner.Requests.Select(r => r.RequestUri!.AbsolutePath).Should().Equal("/first", "/second");
    }

    [Fact]
    public async Task A_307_keeps_the_method_and_the_body()
    {
        var inner = new ScriptedHandler(
            Redirect(HttpStatusCode.TemporaryRedirect, "/moved"),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new SafeRedirectHandler { InnerHandler = inner });

        await client.PostAsync("https://partner.example.com/hook", new StringContent("{\"a\":1}"));

        inner.Requests[1].Method.Should().Be(HttpMethod.Post);
        inner.Bodies[1].Should().Be("{\"a\":1}");
    }

    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
            return responses[Math.Min(_next++, responses.Length - 1)];
        }
    }
}
