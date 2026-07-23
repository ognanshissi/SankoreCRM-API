namespace Sankore.Shared.Kernel;

/// <summary>
/// Thrown when an aggregate invariant is violated at construction time
/// (i.e. a programming error, not an expected business rule failure).
/// Expected business failures should use Result.Fail instead of throwing.
/// </summary>
public sealed class DomainException(string message) : Exception(message);
