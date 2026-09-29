namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Xunit;

/// <summary>
/// Real User-Agent strings, because the whole difficulty of this parser is that browsers
/// impersonate each other: Edge claims to be Chrome and Safari, Chrome claims to be Safari, and
/// Android claims to be Linux. Every test below would still pass with a naive parser except the
/// ones that pin an ordering — those are the point.
/// </summary>
public sealed class UserAgentParserTests
{
    private const string ChromeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string SafariMac =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15";

    private const string EdgeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.2903.86";

    private const string ChromeAndroid =
        "Mozilla/5.0 (Linux; Android 14; SM-S911B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36";

    private const string SafariIphone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_6_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1";

    private const string ChromeIphone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/131.0.6778.73 Mobile/15E148 Safari/604.1";

    private const string FirefoxLinux =
        "Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0";

    private const string IpadOs =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1";

    private const string SamsungAndroid =
        "Mozilla/5.0 (Linux; Android 13; SM-G991B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/23.0 Chrome/115.0.0.0 Mobile Safari/537.36";

    private const string OperaWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 OPR/115.0.0.0";

    // ── platform ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChromeWindows, LoginPlatform.Windows)]
    [InlineData(EdgeWindows, LoginPlatform.Windows)]
    [InlineData(SafariMac, LoginPlatform.MacOs)]
    [InlineData(FirefoxLinux, LoginPlatform.Linux)]
    [InlineData(ChromeAndroid, LoginPlatform.Android)]
    [InlineData(SafariIphone, LoginPlatform.Ios)]
    [InlineData("Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
        LoginPlatform.ChromeOs)]
    public void Reads_the_platform(string ua, LoginPlatform expected)
        => UserAgentParser.Parse(ua).Platform.Should().Be(expected);

    [Fact]
    public void Android_is_not_filed_as_linux()
    {
        // An Android User-Agent literally contains "Linux". Testing Linux first would put every
        // phone in the tenant under Linux, and nobody would notice for months.
        ChromeAndroid.Should().Contain("Linux");
        UserAgentParser.Parse(ChromeAndroid).Platform.Should().Be(LoginPlatform.Android);
    }

    [Fact]
    public void An_ipad_pretending_to_be_a_mac_is_read_as_ios()
    {
        // iPadOS 13+ reports "Macintosh"; only the Mobile token betrays it.
        IpadOs.Should().Contain("Macintosh");
        var info = UserAgentParser.Parse(IpadOs);
        info.Platform.Should().Be(LoginPlatform.Ios);
        info.ClientKind.Should().Be(LoginClientKind.Mobile);
    }

    // ── browser ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChromeWindows, "Chrome", "131.0.0.0")]
    [InlineData(SafariMac, "Safari", "17.6")]
    [InlineData(EdgeWindows, "Edge", "131.0.2903.86")]
    [InlineData(FirefoxLinux, "Firefox", "133.0")]
    [InlineData(ChromeIphone, "Chrome", "131.0.6778.73")]
    [InlineData(SamsungAndroid, "Samsung Internet", "23.0")]
    [InlineData(OperaWindows, "Opera", "115.0.0.0")]
    public void Reads_the_browser_and_its_version(string ua, string browser, string version)
    {
        var info = UserAgentParser.Parse(ua);
        info.Browser.Should().Be(browser);
        info.BrowserVersion.Should().Be(version);
    }

    [Fact]
    public void Edge_opera_and_samsung_are_not_filed_as_chrome()
    {
        // All three carry a "Chrome/" token; testing Chrome first would erase them entirely.
        EdgeWindows.Should().Contain("Chrome/");
        OperaWindows.Should().Contain("Chrome/");
        SamsungAndroid.Should().Contain("Chrome/");

        UserAgentParser.Parse(EdgeWindows).Browser.Should().Be("Edge");
        UserAgentParser.Parse(OperaWindows).Browser.Should().Be("Opera");
        UserAgentParser.Parse(SamsungAndroid).Browser.Should().Be("Samsung Internet");
    }

    [Fact]
    public void Chrome_is_not_filed_as_safari()
    {
        // Every Chromium browser ends with a "Safari/" token.
        ChromeWindows.Should().Contain("Safari/");
        UserAgentParser.Parse(ChromeWindows).Browser.Should().Be("Chrome");
    }

    [Fact]
    public void Safari_version_comes_from_the_version_token_not_the_safari_token()
    {
        // "Safari/605.1.15" is a WebKit build number, not a Safari version.
        UserAgentParser.Parse(SafariMac).BrowserVersion.Should().Be("17.6");
    }

    // ── mobile or web ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChromeAndroid, LoginClientKind.Mobile)]
    [InlineData(SafariIphone, LoginClientKind.Mobile)]
    [InlineData(SamsungAndroid, LoginClientKind.Mobile)]
    [InlineData(ChromeWindows, LoginClientKind.Web)]
    [InlineData(SafariMac, LoginClientKind.Web)]
    [InlineData(FirefoxLinux, LoginClientKind.Web)]
    public void Tells_mobile_from_web(string ua, LoginClientKind expected)
        => UserAgentParser.Parse(ua).ClientKind.Should().Be(expected);

    [Fact]
    public void A_native_mobile_app_is_still_mobile()
    {
        var info = UserAgentParser.Parse("SankoreMobile/2.1 (Android 14; Pixel 8)");

        info.Platform.Should().Be(LoginPlatform.Android);
        info.ClientKind.Should().Be(LoginClientKind.Mobile);
        info.Browser.Should().BeNull("a native app is not a browser, and inventing one would lie");
    }

    // ── refusing to guess ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_user_agent_yields_unknown_rather_than_a_default(string? ua)
    {
        var info = UserAgentParser.Parse(ua);

        info.Raw.Should().BeNull();
        info.Browser.Should().BeNull();
        info.Platform.Should().Be(LoginPlatform.Unknown);
        info.ClientKind.Should().Be(LoginClientKind.Unknown,
            "a guess in a security log is a lie an investigator cannot see through");
    }

    [Fact]
    public void An_unreadable_user_agent_is_kept_raw_and_left_unknown()
    {
        var info = UserAgentParser.Parse("curl/8.7.1");

        info.Raw.Should().Be("curl/8.7.1");
        info.Platform.Should().Be(LoginPlatform.Unknown);
        info.ClientKind.Should().Be(LoginClientKind.Unknown);
    }

    [Fact]
    public void An_oversized_header_is_truncated_rather_than_stored_whole()
    {
        // The header is attacker-controlled and unbounded.
        var info = UserAgentParser.Parse(new string('x', UserAgentParser.MaxRawLength + 500));

        info.Raw!.Length.Should().Be(UserAgentParser.MaxRawLength);
    }
}
