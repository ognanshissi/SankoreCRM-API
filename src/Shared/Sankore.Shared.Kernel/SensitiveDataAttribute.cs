namespace Sankore.Shared.Kernel;

/// <summary>
/// Marks a command property as sensitive so that <c>SanitizedJsonSerializer</c>
/// replaces its value with <c>"***"</c> before writing to the audit log.
///
/// Usage on record positional parameters:
/// <code>
/// public sealed record LoginCommand(
///     string Email,
///     [property: SensitiveData] string Password
/// ) : IRequest&lt;Result&lt;LoginResult&gt;&gt;, ICommand;
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveDataAttribute : Attribute;
