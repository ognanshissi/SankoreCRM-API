namespace Sankore.Modules.Leads.Features.LogActivity;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record LogActivityCommand(
    Guid LeadId,
    ActivityType Type,
    string Subject,
    Guid PerformedBy,
    string? Notes = null,
    DateTimeOffset? ScheduledAt = null,
    int? DurationMinutes = null,
    ActivityOutcome? Outcome = null,
    string? AttachmentsJson = null,
    string? CtiCallReference = null,
    bool IsSystemGenerated = false,
    double? VisitLatitude = null,
    double? VisitLongitude = null,
    string? VisitPhotoReference = null
) : IRequest<Result<LogActivityResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

public sealed record LogActivityResult(Guid ActivityId, DateTimeOffset PerformedAt);
