namespace Sankore.Shared.Kernel;

/// <summary>
/// Optional marker for commands that affect an identifiable domain resource.
/// When implemented, <see cref="AuditBehavior{TRequest,TResponse}"/> will
/// populate <c>ResourceType</c> and <c>ResourceId</c> on the audit entry,
/// making it possible to query "all audit events for Agency X" efficiently.
///
/// Usage:
/// <code>
/// public sealed record DeleteAgencyCommand(Guid AgencyId)
///     : IRequest&lt;Result&gt;, ICommand, IResourceCommand
/// {
///     public string ResourceType =&gt; "Agency";
///     public string? ResourceId  =&gt; AgencyId.ToString();
/// }
/// </code>
///
/// Set <see cref="ResourceId"/> to <c>null</c> for create commands where the
/// new entity's ID is not yet known at dispatch time.
/// </summary>
public interface IResourceCommand
{
    /// <summary>Domain entity type name, e.g. "Agency", "User", "Lead".</summary>
    string ResourceType { get; }

    /// <summary>
    /// String representation of the entity's primary key.
    /// Null for create commands (ID not yet assigned).
    /// </summary>
    string? ResourceId { get; }
}
