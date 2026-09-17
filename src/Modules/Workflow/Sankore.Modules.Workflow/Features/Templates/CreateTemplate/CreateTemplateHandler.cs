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
        // Allow at most one version-1 template per (tenant, entityType).
        // Subsequent versions must be created via CreateDraft.
        var v1Exists = await db.WorkflowTemplates
            .AnyAsync(t => t.EntityType == request.EntityType && t.Version == 1, ct);

        if (v1Exists)
            return Result.Fail<Guid>(
                $"A workflow template for entity type '{request.EntityType}' already exists. " +
                "Use CreateDraft to add a new version.");

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
