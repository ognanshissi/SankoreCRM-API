namespace Sankore.Modules.Leads.Features.MergeLeads;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

internal sealed class MergeLeadsHandler(LeadsDbContext db, TimeProvider clock)
    : IRequestHandler<MergeLeadsCommand, Result<MergeLeadResult>>
{
    public async Task<Result<MergeLeadResult>> Handle(MergeLeadsCommand cmd, CancellationToken ct)
    {
        var target = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.TargetLeadId, ct);

        if (target is null)
            return Result.Fail<MergeLeadResult>("TARGET_LEAD_NOT_FOUND");

        var source = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.SourceLeadId, ct);

        if (source is null)
            return Result.Fail<MergeLeadResult>("SOURCE_LEAD_NOT_FOUND");

        // ── Guard: incompatible records ───────────────────────────────────────
        if (source.Status == LeadStatus.Archived)
            return Result.Fail<MergeLeadResult>("SOURCE_LEAD_IS_ALREADY_ARCHIVED");

        if (target.Status == LeadStatus.Archived)
            return Result.Fail<MergeLeadResult>("TARGET_LEAD_IS_ARCHIVED");

        if (source.Status == LeadStatus.Converted)
            return Result.Fail<MergeLeadResult>("SOURCE_LEAD_IS_CONVERTED_CANNOT_MERGE");

        if (target.Status == LeadStatus.Converted)
            return Result.Fail<MergeLeadResult>("TARGET_LEAD_IS_CONVERTED_CANNOT_MERGE");

        // ── Field selection: apply source values to target where requested ────
        var prefs           = cmd.FieldPreferences;
        var overriddenFields = new List<string>();

        if (prefs is not null)
        {
            target.ApplyMergeOverrides(
                email:             prefs.TakeEmail             && source.Email             is not null ? source.Email             : null,
                firstName:         prefs.TakeFirstName         && source.FirstName         is not null ? source.FirstName         : null,
                lastName:          prefs.TakeLastName          && source.LastName          is not null ? source.LastName          : null,
                nationalId:        prefs.TakeNationalId        && source.NationalId        is not null ? source.NationalId        : null,
                customerReference: prefs.TakeCustomerReference && source.CustomerReference is not null ? source.CustomerReference : null,
                dateOfBirth:       prefs.TakeDateOfBirth       && source.DateOfBirth.HasValue           ? source.DateOfBirth       : null,
                gender:            prefs.TakeGender             ? source.Gender                         : null,
                desiredAmount:     prefs.TakeDesiredAmount     && source.DesiredAmount     is not null ? source.DesiredAmount     : null,
                comment:           prefs.TakeComment           && source.Comment           is not null ? source.Comment           : null,
                companyName:       prefs.TakeCompanyName       && source.CompanyName       is not null ? source.CompanyName       : null,
                companyEmail:      prefs.TakeCompanyEmail      && source.CompanyEmail      is not null ? source.CompanyEmail      : null,
                companyPhone:      prefs.TakeCompanyPhone      && source.CompanyPhone      is not null ? source.CompanyPhone      : null,
                website:           prefs.TakeWebsite           && source.Website           is not null ? source.Website           : null,
                location:          prefs.TakeLocation          && source.Location          is not null ? source.Location          : null);

            overriddenFields.AddRange(prefs.OverriddenFields());
        }

        // ── Transfer timeline: activities ─────────────────────────────────────
        await db.LeadActivities
            .Where(a => a.LeadId == cmd.SourceLeadId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LeadId, cmd.TargetLeadId), ct);

        // ── Transfer timeline: reminders ──────────────────────────────────────
        await db.LeadReminders
            .Where(r => r.LeadId == cmd.SourceLeadId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeadId, cmd.TargetLeadId), ct);

        // ── Transfer timeline: assignment history ─────────────────────────────
        await db.LeadAssignments
            .Where(a => a.LeadId == cmd.SourceLeadId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LeadId, cmd.TargetLeadId), ct);

        // ── Transfer timeline: score history ──────────────────────────────────
        await db.ScoreHistories
            .Where(s => s.LeadId == cmd.SourceLeadId)
            .ExecuteUpdateAsync(s => s.SetProperty(sh => sh.LeadId, cmd.TargetLeadId), ct);

        // ── Transfer tags (deduplicate) ───────────────────────────────────────
        var existingTargetTags = await db.LeadTags
            .Where(t => t.LeadId == cmd.TargetLeadId)
            .Select(t => t.Tag)
            .ToListAsync(ct);

        if (existingTargetTags.Count > 0)
        {
            await db.LeadTags
                .Where(t => t.LeadId == cmd.SourceLeadId && existingTargetTags.Contains(t.Tag))
                .ExecuteDeleteAsync(ct);
        }

        await db.LeadTags
            .Where(t => t.LeadId == cmd.SourceLeadId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LeadId, cmd.TargetLeadId), ct);

        // ── Archive the source and raise LeadMergedDomainEvent ────────────────
        var mergeResult = source.MergeInto(cmd.TargetLeadId, cmd.MergedBy);
        if (mergeResult.IsFailure)
            return Result.Fail<MergeLeadResult>(mergeResult.Error!);

        // ── Persist audit record ──────────────────────────────────────────────
        var auditRecord = LeadMerge.Create(
            tenantId:         target.TenantId,
            targetLeadId:     cmd.TargetLeadId,
            sourceLeadId:     cmd.SourceLeadId,
            mergedBy:         cmd.MergedBy,
            clock:            clock,
            overriddenFields: overriddenFields);

        db.LeadMerges.Add(auditRecord);
        await db.SaveChangesAsync(ct);

        return Result.Ok(new MergeLeadResult(
            MergeId:          auditRecord.Id,
            TargetLeadId:     cmd.TargetLeadId,
            SourceLeadId:     cmd.SourceLeadId,
            MergedAt:         auditRecord.MergedAt,
            OverriddenFields: overriddenFields));
    }
}
