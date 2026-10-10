namespace Sankore.Modules.Leads.Features.Fatca.SubmitDeclaration;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Fatca.Events;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

internal sealed class SubmitFatcaDeclarationHandler(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    FatcaSubjectPlanner planner,
    ICurrentUser currentUser,
    TimeProvider clock,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher)
    : IRequestHandler<SubmitFatcaDeclarationCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        SubmitFatcaDeclarationCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<Guid>("LEAD_NOT_FOUND");

        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return Result.Fail<Guid>(access.Error!);

        var scope = await planner.ResolveAsync(lead, ct);
        if (scope.IsFailure)
            return Result.Fail<Guid>(scope.Error!);

        // FATCA off means no field is required, exposed OR collected (BNF-B-11). Refusing the
        // write rather than accepting and ignoring it: a declaration stored under a tenant with no
        // obligation is a US TIN held for no lawful reason.
        if (!scope.Value.Enabled)
            return Result.Fail<Guid>(FatcaSubjectPlanner.NotEnabled);

        if (!Enum.TryParse<QualificationSubjectType>(cmd.SubjectType, ignoreCase: true, out var subjectType)
            || !Enum.IsDefined(subjectType))
            return Result.Fail<Guid>("INVALID_SUBJECT_TYPE");

        // The subject must be one FATCA actually applies to — which, when FatcaForCoHolders is
        // off, excludes the household even though those people exist on the lead.
        if (!scope.Value.Subjects.Contains((subjectType, cmd.SubjectId)))
            return Result.Fail<Guid>("FATCA_SUBJECT_NOT_IN_SCOPE");

        if (!Enum.TryParse<FatcaDocumentation>(cmd.Documentation, ignoreCase: true, out var documentation)
            || !Enum.IsDefined(documentation))
            return Result.Fail<Guid>("INVALID_FATCA_DOCUMENTATION");

        var now = clock.GetUtcNow();

        var indicia = new FatcaIndicia(
            cmd.Indicia.UsNationalityOrResidence,
            cmd.Indicia.BornInTheUnitedStates,
            cmd.Indicia.UsAddressOrPoBox,
            cmd.Indicia.UsPhoneNumber,
            cmd.Indicia.StandingOrderToUsAccount,
            cmd.Indicia.PowerOfAttorneyWithUsAddress,
            cmd.Indicia.CareOfOrPosteRestanteAsSoleAddress);

        var recorded = FatcaDeclaration.Record(
            tenantId:                lead.TenantId,
            leadId:                  lead.Id,
            subjectType:             subjectType,
            subjectId:               cmd.SubjectId,
            indicia:                 indicia,
            documentation:           documentation,
            recordedBy:              currentUser.Id,
            now:                     now,
            usTin:                   cmd.UsTin,
            selfCertificationDocRef: cmd.SelfCertificationDocRef,
            evidenceDocRef:          cmd.EvidenceDocRef,
            signedAt:                cmd.SignedAt,
            refusalReason:           cmd.RefusalReason);

        if (recorded.IsFailure)
            return Result.Fail<Guid>(recorded.Error!);

        var declaration = recorded.Value;

        // The previous declaration is HISTORISED, not replaced in place (BNF-B-11). A regulator
        // asking what this person certified in March has to get March's answer.
        var previous = await db.FatcaDeclarations
            .AsTracking()
            .FirstOrDefaultAsync(
                d => d.LeadId == lead.Id
                  && d.SubjectType == subjectType
                  && d.SubjectId == cmd.SubjectId
                  && d.SupersededAt == null,
                ct);

        string? previousStatus = null;

        if (previous is not null)
        {
            previousStatus = previous.Status.ToString();

            // Superseded BEFORE the new row is added, so the partial unique index never sees two
            // active declarations for one person — the ordering matters, not just the pair.
            var superseded = previous.Supersede(declaration.Id, now);
            if (superseded.IsFailure)
                return Result.Fail<Guid>(superseded.Error!);

            // Nothing to announce when the answer has not moved. The event drives a compliance
            // notification, and re-sending it on an unchanged re-certification is noise a
            // compliance officer learns to ignore.
            if (previous.Status == declaration.Status)
                previousStatus = null;
        }

        db.FatcaDeclarations.Add(declaration);

        if (previousStatus is not null || previous is null)
        {
            await publisher.PublishAsync(
                new FatcaStatusChangedIntegrationEvent(
                    TenantId:                 lead.TenantId,
                    LeadId:                   lead.Id,
                    SubjectType:              subjectType.ToString(),
                    SubjectId:                cmd.SubjectId,
                    Status:                   declaration.Status.ToString(),
                    PreviousStatus:           previousStatus,
                    RequiresComplianceReview: declaration.RequiresComplianceReview),
                ct);
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok(declaration.Id);
    }
}
