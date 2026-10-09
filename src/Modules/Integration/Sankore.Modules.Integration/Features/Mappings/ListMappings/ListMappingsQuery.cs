namespace Sankore.Modules.Integration.Features.Mappings.ListMappings;

using MediatR;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Not an <c>ICommand</c>: a query, so it skips the transaction and the audit behaviours.
/// <paramref name="Domain"/> null means every domain of the connection.
/// </summary>
internal sealed record ListMappingsQuery(Guid ConnectionId, MappingDomain? Domain)
    : IRequest<Result<ListMappingsResponse>>;

public sealed record ListMappingsResponse(int TotalCount, IReadOnlyList<MappingDto> Items);
