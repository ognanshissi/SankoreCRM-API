namespace Sankore.Modules.Leads.Features.ListActivities;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record ListActivitiesQuery(
    Guid LeadId,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<IReadOnlyList<ActivityDto>>>;

public sealed record ActivityDto(
    Guid Id,
    ActivityType Type,
    string Subject,
    string? Notes,
    Guid PerformedBy,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset PerformedAt,
    int? DurationMinutes,
    ActivityOutcome? Outcome,
    string? AttachmentsJson = null,
    string? CtiCallReference = null,
    bool IsSystemGenerated = false,
    double? VisitLatitude = null,
    double? VisitLongitude = null,
    string? VisitPhotoReference = null);
