namespace Sankore.Modules.Leads.Features.Tags.AddTag;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class AddTagHandler(LeadsDbContext db)
    : IRequestHandler<AddTagCommand, Result<TagDto>>
{
    public async Task<Result<TagDto>> Handle(
        AddTagCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);
        if (lead is null)
            return Result.Fail<TagDto>("LEAD_NOT_FOUND");

        var normalised = cmd.Tag.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(normalised))
            return Result.Fail<TagDto>("TAG_CANNOT_BE_EMPTY");

        // Idempotent: return the existing tag if already present.
        var existing = await db.LeadTags
            .FirstOrDefaultAsync(t => t.LeadId == cmd.LeadId && t.Tag == normalised, ct);

        if (existing is not null)
            return Result.Ok(new TagDto(existing.Id, existing.Tag, existing.AddedBy, existing.AddedAt));

        var tag = LeadTag.Create(lead.TenantId, lead.Id, normalised, cmd.AddedBy);
        db.LeadTags.Add(tag);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new TagDto(tag.Id, tag.Tag, tag.AddedBy, tag.AddedAt));
    }
}
