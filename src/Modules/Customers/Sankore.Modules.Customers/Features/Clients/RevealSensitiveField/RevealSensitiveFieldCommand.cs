namespace Sankore.Modules.Customers.Features.Clients.RevealSensitiveField;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-11 — the one and only way a clear protected value leaves module M01.
///
/// It is a POST and an <c>ICommand</c> even though it changes no business data: reading a
/// protected field IS the event worth recording, so the request must go through
/// <c>AuditBehavior</c> (who revealed what, on which client) and it must write a
/// <c>SensitiveDataAccessLog</c> row. A GET would also be cached and logged by proxies,
/// which is exactly what must not happen to this payload.
///
/// The command carries no sensitive value — only the NAME of the field being asked for —
/// so the audit entry is safe to keep forever.
/// </summary>
/// <param name="ContactPointId">
/// Which phone / e-mail / address to reveal, when the client has several. Ignored for the
/// fields that live on the client row itself; when omitted for a contact-point field, the
/// active PRIMARY contact point of that type is used.
/// </param>
public sealed record RevealSensitiveFieldCommand(
    Guid ClientId,
    SensitiveField Field,
    Guid? ContactPointId
) : IRequest<Result<RevealSensitiveFieldResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>
/// The clear value, returned exactly once per request. The endpoint sends it with
/// <c>Cache-Control: no-store</c> so no intermediary keeps a copy.
/// </summary>
public sealed record RevealSensitiveFieldResult(
    string Field,
    string Value,
    DateTimeOffset RevealedAt);
