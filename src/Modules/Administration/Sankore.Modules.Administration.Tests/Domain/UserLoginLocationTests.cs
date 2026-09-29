namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Xunit;

public sealed class UserLoginLocationTests
{
    private static UserLoginLocation Create(string? ip, string? userAgent) =>
        UserLoginLocation.Create(
            Guid.NewGuid(), Guid.NewGuid(), location: null, ip, UserAgentParser.Parse(userAgent));

    [Fact]
    public void Records_the_address_and_what_the_user_agent_yielded()
    {
        var entry = Create(
            "197.234.221.10",
            "Mozilla/5.0 (Linux; Android 14; SM-S911B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36");

        entry.IpAddress.Should().Be("197.234.221.10");
        entry.Browser.Should().Be("Chrome");
        entry.BrowserVersion.Should().Be("131.0.0.0");
        entry.Platform.Should().Be(LoginPlatform.Android);
        entry.ClientKind.Should().Be(LoginClientKind.Mobile);
        entry.UserAgent.Should().Contain("SM-S911B", "the raw header is kept for investigation");
    }

    [Fact]
    public void A_login_behind_a_proxy_that_strips_the_address_is_still_recorded()
    {
        // A history that refused the row because it could not read an IP would be worse than one
        // with a gap: the login happened either way.
        var entry = Create(null, null);

        entry.Should().NotBeNull();
        entry.IpAddress.Should().BeNull();
        entry.Platform.Should().Be(LoginPlatform.Unknown);
        entry.ClientKind.Should().Be(LoginClientKind.Unknown);
    }

    [Fact]
    public void Oversized_network_values_are_truncated_rather_than_failing_the_login()
    {
        var entry = Create(new string('9', 200), new string('x', 5_000));

        entry.IpAddress!.Length.Should().Be(UserLoginLocation.MaxIpLength);
        entry.UserAgent!.Length.Should().Be(UserAgentParser.MaxRawLength);
    }

    [Fact]
    public void An_ipv6_address_fits()
    {
        const string ipv6 = "2001:0db8:85a3:0000:0000:8a2e:0370:7334";
        Create(ipv6, null).IpAddress.Should().Be(ipv6);
    }
}
