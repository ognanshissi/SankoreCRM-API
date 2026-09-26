namespace Sankore.Modules.Leads.Features.QualificationTemplates.DuplicateQualificationTemplate;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DuplicateQualificationTemplateHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<DuplicateQualificationTemplateCommand, Result<Guid>>
{
    /// <summary>Matches the Name column in <c>QualificationTemplateConfiguration</c>.</summary>
    private const int MaxNameLength = 200;

    private const string CopySuffix = " (copy)";

    public async Task<Result<Guid>> Handle(
        DuplicateQualificationTemplateCommand cmd, CancellationToken ct)
    {
        // Read-only on the source — the copy is a brand new aggregate, so no tracking needed.
        // The tenant query filter keeps this from reaching another tenant's template.
        var source = await db.QualificationTemplates
            .Include(t => t.Sections)
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);

        if (source is null)
            return Result.Fail<Guid>("TEMPLATE_NOT_FOUND");

        var name = string.IsNullOrWhiteSpace(cmd.Name)
            ? DeriveCopyName(source.Name)
            : cmd.Name.Trim();

        var copy = source.Duplicate(name, clock.GetUtcNow());

        db.QualificationTemplates.Add(copy);
        await db.SaveChangesAsync(ct);

        return Result.Ok(copy.Id);
    }

    /// <summary>
    /// "Onboarding" → "Onboarding (copy)", trimming the base name when needed so the
    /// result still fits the Name column instead of failing the insert.
    /// </summary>
    private static string DeriveCopyName(string sourceName)
    {
        var available = MaxNameLength - CopySuffix.Length;

        return sourceName.Length <= available
            ? sourceName + CopySuffix
            : sourceName[..available].TrimEnd() + CopySuffix;
    }
}
