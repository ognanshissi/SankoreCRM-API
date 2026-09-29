namespace Sankore.Modules.Administration.Domain;

/// <summary>Operating system a login came from, as read from the User-Agent header.</summary>
public enum LoginPlatform
{
    /// <summary>No User-Agent, or one this parser does not recognise. Never guessed.</summary>
    Unknown,
    Windows,
    MacOs,
    Linux,
    Android,
    Ios,
    ChromeOs,
}

/// <summary>
/// Whether the session came from a mobile device or a desktop browser. Mobile covers a phone or
/// tablet whether it connected through a browser or a native app; the raw User-Agent is stored
/// alongside for anything finer.
/// </summary>
public enum LoginClientKind
{
    Unknown,
    Web,
    Mobile,
}
