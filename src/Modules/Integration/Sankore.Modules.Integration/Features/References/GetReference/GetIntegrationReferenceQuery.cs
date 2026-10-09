namespace Sankore.Modules.Integration.Features.References.GetReference;

using MediatR;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// "What does this external system call that entity?" — INT-07 over HTTP.
///
/// <para>
/// A query: no <c>ICommand</c>, so no transaction and no audit row. Reading a correspondence is
/// not a mutation, and auditing it would add one row per screen that shows a CBS reference next
/// to a customer.
/// </para>
/// </summary>
internal sealed record GetIntegrationReferenceQuery(
    Guid ConnectionId, string EntityType, Guid CrmId) : IRequest<Result<IntegrationReferenceDto>>;

/// <summary>
/// <paramref name="ExternalId"/> is <c>null</c> when the entity has no counterpart on that
/// connection yet — answered as a 200 and not a 404, because "not created there yet" is an
/// ordinary state of every entity before its command runs, and a screen has to be able to tell
/// that apart from a connection that does not exist.
/// </summary>
internal sealed record IntegrationReferenceDto(
    Guid ConnectionId, string EntityType, Guid CrmId, string? ExternalId);

internal sealed class GetIntegrationReferenceHandler(ReferenceLookup lookup, ICurrentUser currentUser)
    : IRequestHandler<GetIntegrationReferenceQuery, Result<IntegrationReferenceDto>>
{
    public async Task<Result<IntegrationReferenceDto>> Handle(
        GetIntegrationReferenceQuery request, CancellationToken ct)
    {
        var externalId = await lookup.GetExternalIdAsync(
            currentUser.TenantId, request.ConnectionId, request.EntityType, request.CrmId, ct);

        return Result.Ok(new IntegrationReferenceDto(
            request.ConnectionId, request.EntityType, request.CrmId, externalId));
    }
}
