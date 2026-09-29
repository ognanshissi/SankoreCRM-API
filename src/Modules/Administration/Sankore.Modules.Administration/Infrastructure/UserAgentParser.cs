namespace Sankore.Modules.Administration.Infrastructure;

using System.Text.RegularExpressions;
using Sankore.Modules.Administration.Domain;

/// <summary>
/// Turns a raw <c>User-Agent</c> header into the few facts a login history actually needs:
/// which browser, which platform, and whether the session came from a mobile device.
///
/// User-Agent strings are a compatibility museum — nearly every browser claims to be several
/// others — so ORDER OF TESTS IS THE WHOLE ALGORITHM and each rule below says what it must run
/// before. Getting it wrong does not throw; it quietly files every Android login under Linux.
///
/// Nothing here is authoritative: the raw string is stored alongside the parsed values so a
/// wrong guess can always be re-read, and anything unrecognised is <c>Unknown</c> rather than a
/// plausible-looking default.
/// </summary>
public static partial class UserAgentParser
{
    /// <summary>Raw headers are stored truncated: a header is attacker-controlled and unbounded.</summary>
    public const int MaxRawLength = 512;

    public static UserAgentInfo Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return new UserAgentInfo(null, null, null, LoginPlatform.Unknown, LoginClientKind.Unknown);

        var raw = userAgent.Length > MaxRawLength ? userAgent[..MaxRawLength] : userAgent;

        var platform = ParsePlatform(raw);
        var (browser, version) = ParseBrowser(raw);
        var kind = ParseClientKind(raw, platform, browser);

        return new UserAgentInfo(raw, browser, version, platform, kind);
    }

    private static LoginPlatform ParsePlatform(string ua)
    {
        // Android FIRST: an Android UA also contains "Linux", so testing Linux first files every
        // phone under Linux.
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return LoginPlatform.Android;

        // iPadOS 13+ reports itself as "Macintosh" and is only distinguishable by the Mobile or
        // Touch tokens, so this has to come before the macOS test.
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPod", StringComparison.OrdinalIgnoreCase)
            || (ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
                && ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase)))
            return LoginPlatform.Ios;

        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return LoginPlatform.Windows;

        // ChromeOS before Linux for the same reason as Android.
        if (ua.Contains("CrOS", StringComparison.OrdinalIgnoreCase))
            return LoginPlatform.ChromeOs;

        if (ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase))
            return LoginPlatform.MacOs;

        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("X11", StringComparison.OrdinalIgnoreCase))
            return LoginPlatform.Linux;

        return LoginPlatform.Unknown;
    }

    /// <summary>
    /// Most browsers impersonate Chrome, and Chrome impersonates Safari, so the specific names
    /// must be tested before the generic ones. Reordering this list silently mislabels traffic.
    /// </summary>
    private static (string? Browser, string? Version) ParseBrowser(string ua)
    {
        // Edge says "Edg/", and also "Chrome" and "Safari".
        if (Version(ua, "Edg") is { } edge) return ("Edge", edge);
        if (Version(ua, "EdgiOS") is { } edgeIos) return ("Edge", edgeIos);
        if (Version(ua, "EdgA") is { } edgeAndroid) return ("Edge", edgeAndroid);

        // Opera says "OPR/" and also "Chrome".
        if (Version(ua, "OPR") is { } opera) return ("Opera", opera);

        // Samsung Internet says "SamsungBrowser/" and also "Chrome".
        if (Version(ua, "SamsungBrowser") is { } samsung) return ("Samsung Internet", samsung);

        // Firefox on iOS is "FxiOS", not "Firefox".
        if (Version(ua, "FxiOS") is { } firefoxIos) return ("Firefox", firefoxIos);
        if (Version(ua, "Firefox") is { } firefox) return ("Firefox", firefox);

        // Chrome on iOS is "CriOS", not "Chrome".
        if (Version(ua, "CriOS") is { } chromeIos) return ("Chrome", chromeIos);
        if (Version(ua, "Chrome") is { } chrome) return ("Chrome", chrome);

        // Safari LAST: every Chromium browser carries a "Safari/" token. Its real version lives
        // in "Version/", not in "Safari/" — that one is the WebKit build number.
        if (ua.Contains("Safari", StringComparison.OrdinalIgnoreCase))
            return ("Safari", Version(ua, "Version"));

        return (null, null);
    }

    /// <summary>
    /// Decides the one thing the operator asked for: mobile, or web.
    ///
    /// "Mobile" means the session came from a phone or tablet, whether through a browser or a
    /// native app. "Web" means a desktop browser. A user agent we cannot read at all stays
    /// Unknown rather than being filed as web, because a guess here is a lie in a security log.
    /// </summary>
    private static LoginClientKind ParseClientKind(string ua, LoginPlatform platform, string? browser)
    {
        if (platform is LoginPlatform.Android or LoginPlatform.Ios)
            return LoginClientKind.Mobile;

        // "Mobi" is the token the HTML spec tells sites to look for; it also catches phones on
        // platforms this parser does not recognise.
        if (ua.Contains("Mobi", StringComparison.OrdinalIgnoreCase))
            return LoginClientKind.Mobile;

        if (platform is LoginPlatform.Windows or LoginPlatform.MacOs
                     or LoginPlatform.Linux or LoginPlatform.ChromeOs)
            return LoginClientKind.Web;

        // A recognised browser on an unrecognised platform is still a browser.
        return browser is null ? LoginClientKind.Unknown : LoginClientKind.Web;
    }

    /// <summary>Reads the version that follows "<c>token/</c>", or null when the token is absent.</summary>
    private static string? Version(string ua, string token)
    {
        var match = Regex.Match(
            ua,
            $@"(?:^|[\s;(])(?:{Regex.Escape(token)})/([0-9]+(?:\.[0-9]+)*)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>What a User-Agent header yielded. <paramref name="Raw"/> is kept so nothing is lost.</summary>
public sealed record UserAgentInfo(
    string? Raw,
    string? Browser,
    string? BrowserVersion,
    LoginPlatform Platform,
    LoginClientKind ClientKind);
