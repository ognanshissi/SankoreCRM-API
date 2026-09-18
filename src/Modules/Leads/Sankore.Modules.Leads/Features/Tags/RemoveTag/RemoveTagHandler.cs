namespace Sankore.Modules.Leads.Features.Tags.RemoveTag;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class RemoveTagHandler(LeadsDbContext db)
    : IRequestHandler<RemoveTagCommand, Result>
{
    public async Task<Result> Handle(
        RemoveTagCommand cmd, CancellationToken ct)
    {
        var normalised = cmd.Tag.Trim().ToLowerInvariant();

        var tag = await db.LeadTags
            .FirstOrDefaultAsync(
                t => t.LeadId == cmd.LeadId && t.Tag == normalised, ct);

        if (tag is null)
            return Result.Fail("TAG_NOT_FOUND");

        db.LeadTags.Remove(tag);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
