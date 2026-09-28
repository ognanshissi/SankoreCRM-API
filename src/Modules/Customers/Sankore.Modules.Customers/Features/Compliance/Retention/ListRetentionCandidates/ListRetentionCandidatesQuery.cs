namespace Sankore.Modules.Customers.Features.Compliance.Retention.ListRetentionCandidates;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads the anonymization proposals materialized by
/// <see cref="IdentifyRetentionCandidatesJob"/>. A query: no <c>ICommand</c>, no audit entry —
/// only the anonymization itself is audited.
/// </summary>
public sealed record ListRetentionCandidatesQuery(int Page = 1, int PageSize = 20)
    : IRequest<Result<PagedResult<RetentionCandidateDto>>>;

/// <summary>
/// One archived client whose retention period has elapsed and whose KYC file is cleared.
/// <para>
/// No identity field is exposed, masked or otherwise: a compliance officer deciding on an
/// erasure needs the client number, the agency and the archive dates — not the identity of the
/// person being erased.
/// </para>
/// </summary>
public sealed record RetentionCandidateDto(
    Guid ClientId,
    string ClientNumber,
    string ClientType,
    Guid AgencyId,
    string AgencyCode,
    DateTimeOffset? ArchivedAt,
    string? ArchiveReason,
    DateTimeOffset IdentifiedAt);
