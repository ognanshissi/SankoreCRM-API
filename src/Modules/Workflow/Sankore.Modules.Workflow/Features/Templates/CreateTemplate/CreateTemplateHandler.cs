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
        // Allow at most one version-1 template per (tenant, entityType, circuit).
        // Subsequent versions must be created via CreateDraft. A project template is a circuit of
        // its own, keyed by its name; every other entity type has the single empty circuit.
        var circuit = WorkflowTemplate.CircuitFor(request.EntityType, request.Name);
        var v1Exists = await db.WorkflowTemplates
            .AnyAsync(t => t.EntityType == request.EntityType && t.Circuit == circuit && t.Version == 1, ct);

        if (v1Exists)
            return Result.Fail<Guid>(circuit.Length > 0
                ? $"A project workflow named like '{request.Name}' already exists. Choose another name, or create a new version of it."
                : $"A workflow template for entity type '{request.EntityType}' already exists. " +
                  "Use CreateDraft to add a new version.");

        var template = WorkflowTemplate.Create(
            currentUser.TenantId,
            request.EntityType,
            request.Name,
            currentUser.Id,
            request.Description,
            circuit);

        db.WorkflowTemplates.Add(template);
        await db.SaveChangesAsync(ct);

        return Result.Ok(template.Id);
    }
}
