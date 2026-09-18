namespace Sankore.Modules.Leads.Features.Tags.ListTags;

using MediatR;
using Sankore.Modules.Leads.Features.Tags.AddTag;
using Sankore.Shared.Kernel;

internal sealed record ListTagsQuery(Guid LeadId)
    : IRequest<Result<IReadOnlyList<TagDto>>>;
