namespace Sankore.Modules.Integration.Features.Mappings.UpsertMapping;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates or replaces one mapping. PUT, and an upsert, because the identity of a mapping is
/// <c>(connection, domain, crm_code)</c> — which is in the URL — and not a surrogate id the
/// caller would have to look up first.
///
/// <para>
/// A duplicate <c>(connection, domain, crm_code)</c> is therefore an UPDATE, not an error: the
/// unique index exists to keep one translation per CRM code, not to refuse the second edit of
/// one. A duplicate EXTERNAL code is allowed outright — two CRM products may legitimately fold
/// onto one CBS product, which is why the reverse index is not unique.
/// </para>
/// </summary>
internal sealed record UpsertMappingCommand(
    Guid ConnectionId,
    MappingDomain Domain,
    string CrmCode,
    string ExternalCode,
    string? Label)
    : IRequest<Result<UpsertMappingResponse>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationMapping";

    // The audit row points at the connection: the mapping's own id does not exist yet on a
    // create, and the connection is what an auditor filters on.
    public string? ResourceId => ConnectionId.ToString();
}

/// <param name="Created">True when the row did not exist. The caller's screen says "added" or "updated".</param>
public sealed record UpsertMappingResponse(bool Created, MappingDto Mapping);

/// <summary>Body of the PUT. The domain and the CRM code are in the route, not here.</summary>
public sealed record UpsertMappingRequest(string ExternalCode, string? Label);
