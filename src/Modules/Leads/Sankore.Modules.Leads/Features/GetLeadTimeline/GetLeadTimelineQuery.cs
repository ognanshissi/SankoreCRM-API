namespace Sankore.Modules.Leads.Features.GetLeadTimeline;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

public sealed record GetLeadTimelineQuery(Guid LeadId)
    : ILeadReadRequest, IRequest<Result<IReadOnlyList<TimelineEvent>>>;
