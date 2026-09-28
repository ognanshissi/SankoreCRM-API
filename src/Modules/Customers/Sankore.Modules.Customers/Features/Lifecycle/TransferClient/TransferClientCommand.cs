namespace Sankore.Modules.Customers.Features.Lifecycle.TransferClient;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Moves a client to another agency (US-M01-BE-16).
///
/// Implements <see cref="IAgencyScopedRequest"/> so that
/// <c>AgencyAuthorizationBehavior</c> rejects a transfer towards an agency outside
/// the caller's perimeter with <c>AGENCY_OUT_OF_SCOPE</c> BEFORE the handler runs —
/// an operator cannot push a client into a branch they do not administer. The
/// handler additionally checks the SOURCE agency, which the behavior cannot see.
///
/// The interface member is implemented explicitly because the record property is a
/// non-nullable <c>Guid</c> (a transfer always has a destination) while the
/// contract exposes <c>Guid?</c>.
/// No sensitive field: identifiers plus an operator motive.
/// </summary>
public sealed record TransferClientCommand(Guid ClientId, Guid TargetAgencyId, string Reason)
    : IRequest<Result<ClientLifecycleStateDto>>, ICommand, IResourceCommand, IAgencyScopedRequest
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();

    Guid? IAgencyScopedRequest.TargetAgencyId => TargetAgencyId;
}
