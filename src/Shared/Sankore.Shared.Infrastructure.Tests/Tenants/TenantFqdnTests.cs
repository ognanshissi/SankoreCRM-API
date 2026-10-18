namespace Sankore.Shared.Infrastructure.Tests.Tenants;

using FluentAssertions;
using Sankore.Shared.Infrastructure.Tenants;
using Xunit;

/// <summary>
/// What a tenant's FQDN is allowed to be.
///
/// <para>
/// <b>Why this suite exists.</b> A tenant is found by comparing the stored <c>tenants.fqdn</c>
/// with what the caller sent, as a WHOLE string, and what a browser sends is
/// <c>location.origin</c> — scheme included, never a trailing slash. Two values that look right
/// to an operator therefore do not work: the bare host <c>crm.example.com</c> (which
/// docs/deployment-dokploy.md recommended) and the URL copied from a browser bar,
/// <c>https://crm.example.com/</c>. Neither fails loudly — the deployment comes up and every
/// request answers 400 "Cannot determine tenant", with nothing wrong in any log. These two rules
/// are the whole reason the seeders refuse one and normalise the other.
/// </para>
/// </summary>
public sealed class TenantFqdnTests
{
    [Theory]
    [InlineData("https://crm.example.com/", "https://crm.example.com")]
    [InlineData("  https://CRM.Example.com  ", "https://crm.example.com")]
    [InlineData("HTTPS://CRM.EXAMPLE.COM", "https://crm.example.com")]
    [InlineData("http://localhost:4222", "http://localhost:4222")]
    [InlineData(null, "")]
    public void Normalize_strips_the_trailing_slash_and_lowercases(string? input, string expected)
        => TenantFqdn.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("https://crm.example.com")]
    [InlineData("https://crm.example.com/")]
    [InlineData("http://localhost:4222")]
    public void IsOrigin_accepts_an_http_origin(string value)
    {
        TenantFqdn.IsOrigin(value, out var problem).Should().BeTrue();
        problem.Should().BeNull();
    }

    [Theory]
    // The bare host the deployment guide used to recommend: it can never match location.origin.
    [InlineData("crm.example.com")]
    [InlineData("api-crm.example.com")]
    // A path is not part of an origin, and a stored one would never be sent either.
    [InlineData("https://crm.example.com/app")]
    // Neither of these reaches a browser.
    [InlineData("ftp://crm.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void IsOrigin_refuses_anything_a_browser_would_not_send(string? value)
    {
        TenantFqdn.IsOrigin(value, out var problem).Should().BeFalse();
        problem.Should().NotBeNullOrWhiteSpace();
    }
}
