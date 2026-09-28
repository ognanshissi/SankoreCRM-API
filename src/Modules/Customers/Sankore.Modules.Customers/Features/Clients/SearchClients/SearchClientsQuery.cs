namespace Sankore.Modules.Customers.Features.Clients.SearchClients;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-12 — multi-criteria client search. Filters are CUMULATIVE (AND).
///
/// A query, so no <c>ICommand</c>: searching is not audited (opening a specific record's
/// protected field is, through the reveal endpoint).
///
/// <see cref="Phone"/> and <see cref="IdentityDocumentNumber"/> are matched by EQUALITY ON
/// THE BLIND INDEX after normalization, so "+225 07 08 09 18" and "07080918" find the same
/// client and the encrypted columns are never scanned or decrypted to answer.
///
/// <see cref="Name"/> is a PREFIX search on the denormalized <c>SearchKey</c> column
/// (upper-cased, accent-free, surname first), so it stays an indexable <c>LIKE 'TERM%'</c>.
/// </summary>
public sealed record SearchClientsQuery(
    string? ClientNumber,
    string? Phone,
    string? IdentityDocumentNumber,
    string? Name,
    ClientStatus? Status,
    Guid? AgencyId,
    Guid? AdvisorUserId,
    ClientType? Type,
    string? SegmentCode,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<PagedResult<ClientSearchItemDto>>>;
