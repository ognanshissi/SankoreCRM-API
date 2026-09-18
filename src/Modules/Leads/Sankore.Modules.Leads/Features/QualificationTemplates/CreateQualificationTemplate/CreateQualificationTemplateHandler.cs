namespace Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateQualificationTemplateHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<CreateQualificationTemplateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateQualificationTemplateCommand cmd, CancellationToken ct)
    {
        var template = QualificationTemplate.Create(
            tenantId:    cmd.TenantId,
            name:        cmd.Name,
            now:         clock.GetUtcNow(),
            description: cmd.Description,
            productName: cmd.ProductName);

        foreach (var q in cmd.Questions)
            template.AddQuestion(q.Label, q.Type, q.Weight, q.IsRequired, q.Options);

        db.QualificationTemplates.Add(template);
        await db.SaveChangesAsync(ct);

        return Result.Ok(template.Id);
    }
}
