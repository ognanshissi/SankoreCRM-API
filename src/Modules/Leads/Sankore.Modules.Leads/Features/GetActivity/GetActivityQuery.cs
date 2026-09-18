namespace Sankore.Modules.Leads.Features.GetActivity;

using MediatR;
using Sankore.Modules.Leads.Features.ListActivities;
using Sankore.Shared.Kernel;

internal sealed record GetActivityQuery(Guid LeadId, Guid ActivityId)
    : IRequest<Result<ActivityDto>>;
