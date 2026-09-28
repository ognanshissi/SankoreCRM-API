namespace Sankore.Modules.Customers.Features.Duplicates.ListDuplicateCandidates;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;
using Sankore.Shared.Kernel;

/// <summary>
/// Review queue of the pairs the nightly detection flagged (US-M01-BE-24).
/// A query: no <see cref="Sankore.Shared.Infrastructure.Behaviors.ICommand"/>, so neither the
/// transaction nor the audit behavior wraps it.
/// </summary>
public sealed record ListDuplicateCandidatesQuery(
    DuplicateCandidateStatus? Status,
    int Page = 1,
    int PageSize = 20) : IRequest<Result<PagedResult<DuplicateCandidateDto>>>;

/// <summary>
/// One side of a flagged pair. Identity label and client number only — the reviewer opens the client
/// file for anything protected, which keeps that read audited.
/// </summary>
public sealed record DuplicateCandidateClientDto(
    Guid ClientId,
    string ClientNumber,
    string DisplayName,
    string ClientType,
    string Status,
    Guid AgencyId,
    string AgencyCode,
    string KycStatus);

/// <summary>A flagged pair with its score and the criteria that produced it.</summary>
public sealed record DuplicateCandidateDto(
    Guid Id,
    int Score,
    string Status,
    IReadOnlyList<DuplicateReasonDto> Reasons,
    DateTimeOffset DetectedAt,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    DuplicateCandidateClientDto ClientA,
    DuplicateCandidateClientDto ClientB);
