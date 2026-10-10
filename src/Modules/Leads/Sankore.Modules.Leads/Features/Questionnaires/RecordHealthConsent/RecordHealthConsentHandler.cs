namespace Sankore.Modules.Leads.Features.Questionnaires.RecordHealthConsent;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class RecordHealthConsentHandler(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    HealthConsentGuard consentGuard,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<RecordHealthConsentCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RecordHealthConsentCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<Guid>("LEAD_NOT_FOUND");

        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return Result.Fail<Guid>(access.Error!);

        if (!Enum.TryParse<QualificationSubjectType>(cmd.SubjectType, ignoreCase: true, out var subjectType)
            || !Enum.IsDefined(subjectType))
            return Result.Fail<Guid>("INVALID_SUBJECT_TYPE");

        var subjectExists = subjectType switch
        {
            QualificationSubjectType.Principal       => cmd.SubjectId == lead.Id,
            QualificationSubjectType.HouseholdMember =>
                lead.ActiveHouseholdMembers.Any(m => m.Id == cmd.SubjectId),
            _ => false,
        };

        if (!subjectExists)
            return Result.Fail<Guid>("QUALIFICATION_SUBJECT_NOT_FOUND");

        // Idempotent on an ACTIVE consent for the same person: a double-submitted form must not
        // produce two rows, which would then both have to be withdrawn to stop collection.
        var already = await db.LeadConsents.AnyAsync(
            c => c.LeadId == lead.Id
              && c.Type == ConsentType.HealthData
              && c.Status == ConsentStatus.Active
              && c.SubjectType == subjectType
              && c.SubjectId == cmd.SubjectId,
            ct);

        if (already)
        {
            var existing = await db.LeadConsents
                .Where(c => c.LeadId == lead.Id
                         && c.Type == ConsentType.HealthData
                         && c.Status == ConsentStatus.Active
                         && c.SubjectType == subjectType
                         && c.SubjectId == cmd.SubjectId)
                .Select(c => c.Id)
                .FirstAsync(ct);

            return Result.Ok(existing);
        }

        // Who may give it, through the SAME rule BNF-B-08 applies to a questionnaire respondent:
        // required for a minor, and then the principal or the spouse. One definition of "who may
        // act for a child", shared rather than restated — and it is what stops an arbitrary id
        // being recorded as having consented on a child's behalf.
        var member = lead.HouseholdMembers.FirstOrDefault(m => m.Id == cmd.SubjectId);
        var age = member?.Identity.AgeAt(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        var giver = consentGuard.EnsureRespondentValid(
            lead, subjectType, cmd.SubjectId, cmd.GivenById, age);
        if (giver.IsFailure)
            return Result.Fail<Guid>(giver.Error!);

        // An adult who names a giver must name themselves or somebody of the household: a consent
        // attributed to an unidentifiable id is not evidence of anything.
        if (cmd.GivenById is { } named
            && named != lead.Id
            && named != cmd.SubjectId
            && !lead.ActiveHouseholdMembers.Any(m => m.Id == named))
            return Result.Fail<Guid>("INVALID_CONSENT_GIVER");

        var channel = Enum.Parse<ConsentChannel>(cmd.Channel, ignoreCase: true);

        var consent = LeadConsent.Create(
            tenantId:       lead.TenantId,
            leadId:         lead.Id,
            type:           ConsentType.HealthData,
            channel:        channel,
            // ALWAYS the authenticated actor. Never cmd.GivenById: that field is the person who
            // consented, and writing it here would let an agent erase themselves from — or
            // attribute to a colleague — the one record a regulator asks about first.
            recordedBy:     currentUser.Id,
            clock:          clock,
            proofReference: cmd.ProofReference,
            subjectType:    subjectType,
            subjectId:      cmd.SubjectId,
            givenBy:        cmd.GivenById);

        db.LeadConsents.Add(consent);
        await db.SaveChangesAsync(ct);

        return Result.Ok(consent.Id);
    }
}
