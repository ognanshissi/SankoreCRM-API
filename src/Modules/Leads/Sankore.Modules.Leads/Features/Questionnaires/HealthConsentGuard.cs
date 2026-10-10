namespace Sankore.Modules.Leads.Features.Questionnaires;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Refuses a health answer that has no consent behind it (BNF-B-09: "saisie bloquée sans
/// consentement").
/// </summary>
internal sealed class HealthConsentGuard(LeadsDbContext db)
{
    internal const string Missing = "HEALTH_CONSENT_MISSING";
    internal const string RespondentRequired = "MINOR_REQUIRES_RESPONDENT";
    internal const string RespondentInvalid = "MINOR_RESPONDENT_MUST_BE_PARENT";

    /// <summary>
    /// Whether this person's health answers may be recorded.
    /// </summary>
    /// <remarks>
    /// The consent is PER PERSON and must be ACTIVE at the moment of the write — a withdrawn
    /// consent blocks further collection, which is the whole point of being able to withdraw one.
    /// A consent given for the principal does not cover a child.
    /// </remarks>
    internal async Task<Result> EnsureConsentAsync(
        Guid leadId, QualificationSubjectType subjectType, Guid subjectId, CancellationToken ct)
    {
        var consented = await db.LeadConsents.AnyAsync(
            c => c.LeadId == leadId
              && c.Type == ConsentType.HealthData
              && c.Status == ConsentStatus.Active
              && c.SubjectType == subjectType
              && c.SubjectId == subjectId,
            ct);

        return consented ? Result.Ok() : Result.Fail(Missing);
    }

    /// <summary>
    /// Validates who answered for a MINOR (BNF-B-08: "RespondentId obligatoire et égal au principal
    /// ou au conjoint").
    /// </summary>
    /// <param name="minorAge">
    /// Null when the birth date is unknown. The rule is then NOT applied: refusing would block the
    /// declaration over a field BNF-B-01 makes optional, and the completeness check is what
    /// reports the missing date.
    /// </param>
    /// <remarks>
    /// Eighteen is the age of majority across the UEMOA zone and is also what M01 already uses for
    /// its <c>minimum-age</c> setting. Hard-coded rather than read from a tenant parameter because
    /// this is not a commercial policy — it is who may legally make a declaration about a child.
    /// </remarks>
    internal Result EnsureRespondentValid(
        Lead lead, QualificationSubjectType subjectType, Guid subjectId,
        Guid? respondentId, int? minorAge)
    {
        if (subjectType != QualificationSubjectType.HouseholdMember)
            return Result.Ok();

        if (minorAge is not { } age || age >= 18)
            return Result.Ok();

        if (respondentId is not { } respondent)
            return Result.Fail(RespondentRequired);

        // The principal is identified by the lead's own id; the spouse by their member id.
        var spouseId = lead.ActiveHouseholdMembers
            .FirstOrDefault(m => m.Type == HouseholdMemberType.Spouse)?.Id;

        var allowed = respondent == lead.Id || (spouseId is { } spouse && respondent == spouse);

        return allowed ? Result.Ok() : Result.Fail(RespondentInvalid);
    }
}
