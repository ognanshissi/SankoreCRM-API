namespace Sankore.Modules.Customers.Features.Compliance.Retention.AnonymizeClient;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Irreversibly erases the personal data of an archived client whose retention period has
/// elapsed (US-M01-BE-29).
/// <para>
/// <see cref="ICommand"/> + <see cref="IResourceCommand"/> is not optional here: the audit entry
/// naming the client and the reason is the only trace left once the data is gone.
/// </para>
/// <para>
/// <see cref="Reason"/> is a compliance justification (a regulator's reference, a subject
/// request id) and is deliberately NOT marked <c>[SensitiveData]</c> — it must survive in clear
/// in the audit trail, so the caller must keep personal data out of it.
/// </para>
/// </summary>
public sealed record AnonymizeClientCommand(Guid ClientId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}
