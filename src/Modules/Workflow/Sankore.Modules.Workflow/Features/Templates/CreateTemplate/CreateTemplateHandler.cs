using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Templates.CreateTemplate;

internal sealed class CreateTemplateHandler(
    WorkflowDbContext db,
    ICurrentUser currentUser) : IRequestHandler<CreateTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateTemplateCommand request, CancellationToken ct)
    {
        // Only one active template per (tenant, entityType) — check for inactive duplicates
        // too so the user knows one already exists.
        var exists = await db.WorkflowTemplates
            .AnyAsync(t => t.EntityType == request.EntityType, ct);

        if (exists)
            return Result.Fail<Guid>(
                $"A workflow template for entity type '{request.EntityType}' already exists in this tenant.");

        var template = WorkflowTemplate.Create(
            currentUser.TenantId,
            request.EntityType,
            request.Name,
            currentUser.Id,
            request.Description);

        db.WorkflowTemplates.Add(template);
        await db.SaveChangesAsync(ct);

        return Result.Ok(template.Id);
    }
}
