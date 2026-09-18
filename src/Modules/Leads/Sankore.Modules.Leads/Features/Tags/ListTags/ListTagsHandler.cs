namespace Sankore.Modules.Leads.Features.Tags.ListTags;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.Tags.AddTag;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListTagsHandler(LeadsDbContext db)
    : IRequestHandler<ListTagsQuery, Result<IReadOnlyList<TagDto>>>
{
    public async Task<Result<IReadOnlyList<TagDto>>> Handle(
        ListTagsQuery query, CancellationToken ct)
    {
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<TagDto>>("LEAD_NOT_FOUND");

        var tags = await db.LeadTags
            .Where(t => t.LeadId == query.LeadId)
            .OrderBy(t => t.Tag)
            .Select(t => new TagDto(t.Id, t.Tag, t.AddedBy, t.AddedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<TagDto>>(tags);
    }
}
