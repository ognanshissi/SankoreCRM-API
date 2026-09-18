namespace Sankore.Modules.Leads.Features.GetLeadTimeline;

using MediatR;
using Sankore.Shared.Kernel;

public sealed record GetLeadTimelineQuery(Guid LeadId)
    : IRequest<Result<IReadOnlyList<TimelineEvent>>>;
