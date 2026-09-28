namespace Sankore.Modules.Customers.Features.Duplicates.Merge.ListClientMerges;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;
using Sankore.Shared.Kernel;

/// <summary>Paginated merge requests, most recent first. A read: no <c>ICommand</c>.</summary>
public sealed record ListClientMergesQuery(
    MergeRequestStatus? Status,
    int Page = 1,
    int PageSize = 20) : IRequest<Result<PagedResult<ClientMergeRequestDto>>>;
