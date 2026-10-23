namespace Sankore.Modules.Leads.Features.Tags.ListTags;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Features.Tags.AddTag;
using Sankore.Shared.Kernel;

internal sealed record ListTagsQuery(Guid LeadId)
    : ILeadReadRequest, IRequest<Result<IReadOnlyList<TagDto>>>;
