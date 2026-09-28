namespace Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>Detail of one merge request. A read: no <c>ICommand</c>, no transaction, no audit row.</summary>
public sealed record GetClientMergeQuery(Guid MergeRequestId) : IRequest<Result<ClientMergeRequestDto>>;

/// <summary>
/// A merge request as the review screen shows it. Both clients are described by their number and
/// display name only — anything protected stays behind the audited reveal endpoint.
/// </summary>
public sealed record ClientMergeRequestDto(
    Guid Id,
    string Status,
    Guid SurvivorClientId,
    string SurvivorClientNumber,
    string SurvivorDisplayName,
    Guid AbsorbedClientId,
    string AbsorbedClientNumber,
    string AbsorbedDisplayName,
    IReadOnlyDictionary<string, string> FieldChoices,
    Guid? WorkflowInstanceId,
    Guid RequestedBy,
    DateTimeOffset RequestedAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionComment,
    DateTimeOffset? ExecutedAt);
