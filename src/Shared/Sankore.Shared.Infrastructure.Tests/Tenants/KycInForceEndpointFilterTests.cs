namespace Sankore.Shared.Infrastructure.Tests.Tenants;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>KYC routes stay readable, and only readable, once the institution switched KYC off (docs/kyc-par-institution.md).</summary>
public sealed class KycInForceEndpointFilterTests
{
    private static async Task<(bool Reached, object? Result)> FilterAsync(string method, ITenantKyc kyc, string path = "/api/v1/kyc-files")
    {
        var services = new ServiceCollection();
        services.AddSingleton(kyc);
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Guid.NewGuid()));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = method;
        http.Request.Path = path;

        var reached = false;
        var result = await new KycInForceEndpointFilter(h => h.Request.Path.Value!.EndsWith("/revoke", StringComparison.Ordinal))
            .InvokeAsync(new DefaultEndpointFilterInvocationContext(http),
                _ => { reached = true; return ValueTask.FromResult<object?>(Results.Ok()); });
        return (reached, result);
    }

    [Fact]
    public async Task In_force_everything_goes_through()
        => (await FilterAsync(HttpMethods.Post, FixedTenantKyc.InForce)).Reached.Should().BeTrue();

    [Fact]
    public async Task Switched_off_a_read_goes_through()
        => (await FilterAsync(HttpMethods.Get, FixedTenantKyc.Off)).Reached.Should().BeTrue();

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Switched_off_a_write_is_refused_with_its_code(string method)
    {
        var (reached, result) = await FilterAsync(method, FixedTenantKyc.Off);

        reached.Should().BeFalse();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task An_exempted_write_goes_through()
        => (await FilterAsync(HttpMethods.Post, FixedTenantKyc.Off, "/api/v1/kyc-files/x/remote-invitations/y/revoke"))
            .Reached.Should().BeTrue();
}
