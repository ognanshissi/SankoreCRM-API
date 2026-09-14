namespace Sankore.Shared.Kernel;

/// <summary>
/// Thrown when an aggregate invariant is violated at construction time
/// (i.e. a programming error, not an expected business rule failure).
/// Expected business failures should use Result.Fail instead of throwing.
///
/// When <see cref="MessageKey"/> is set, DomainExceptionHandler translates it via
/// IStringLocalizer&lt;DomainErrors&gt; using the current request culture.
/// When null, the raw <see cref="Exception.Message"/> is returned as-is.
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>
    /// Optional resource key looked up in DomainErrors.{culture}.resx.
    /// If null, the plain <see cref="Exception.Message"/> is used in the response.
    /// </summary>
    public string? MessageKey { get; }

    public DomainException(string message) : base(message) { }

    public DomainException(string message, string messageKey) : base(message)
    {
        MessageKey = messageKey;
    }
}
