using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.CreateDraft;

internal sealed class CreateDraftHandler(WorkflowDbContext db, ICurrentUser currentUser)
    : IRequestHandler<CreateDraftCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateDraftCommand request, CancellationToken ct)
    {
        var source = await db.WorkflowTemplates
            .Include(t => t.Steps)
                .ThenInclude(s => s.Rules)
            .Include(t => t.Transitions)
            .FirstOrDefaultAsync(t => t.Id == request.SourceTemplateId, ct);

        if (source is null)
            return Result.Fail<Guid>($"Template {request.SourceTemplateId} not found.");

        // Guard: only one draft-in-progress per entity type per tenant
        var nextVersion = source.Version + 1;
        var draftExists = await db.WorkflowTemplates
            .AnyAsync(t => t.EntityType == source.EntityType
                        && t.Version == nextVersion
                        && !t.IsActive, ct);

        if (draftExists)
            return Result.Fail<Guid>(
                $"A draft at version {nextVersion} already exists for entity type '{source.EntityType}'. " +
                "Activate or delete it before creating another draft.");

        var draft = source.CreateDraft(currentUser.Id);

        // Adding the template to the DbSet triggers EF graph traversal:
        // steps → rules and transitions are all marked as Added automatically
        // because of UsePropertyAccessMode(PropertyAccessMode.Field) on each navigation.
        db.WorkflowTemplates.Add(draft);
        await db.SaveChangesAsync(ct);

        return Result.Ok(draft.Id);
    }
}
