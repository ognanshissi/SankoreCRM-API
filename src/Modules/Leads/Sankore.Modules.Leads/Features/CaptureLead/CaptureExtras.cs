namespace Sankore.Modules.Leads.Features.CaptureLead;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The household and the questionnaires a v2 capture may carry (BNF-B-18), applied to the lead in
/// the SAME unit of work.
///
/// <para>
/// A separate type because the capture handler is already long and this is a distinct question —
/// but it deliberately does NOT save: it enlists in the handler's transaction, so a refusal here
/// leaves no trace of the capture at all rather than a lead missing the family somebody typed.
/// </para>
/// </summary>
internal static class CaptureExtras
{
    internal const string TemplateNotFound = "QUESTIONNAIRE_TEMPLATE_NOT_FOUND";
    internal const string TemplateNotPublished = "QUESTIONNAIRE_TEMPLATE_NOT_PUBLISHED";

    internal static async Task<Result> ApplyAsync(
        LeadsDbContext db,
        Lead lead,
        CaptureLeadCommand cmd,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var members = await AddHouseholdAsync(lead, cmd, now);
        if (members.IsFailure)
            return Result.Fail(members.Error!);

        return await AddQuestionnairesAsync(db, lead, cmd, members.Value, now, ct);
    }

    /// <summary>
    /// Adds the household members, returning them in REQUEST ORDER so a questionnaire's
    /// <c>SubjectIndex</c> can be resolved to the member it names.
    /// </summary>
    /// <remarks>
    /// <b>No product-line limits are applied</b>, and that is not an omission. The ceilings live on
    /// the line's beneficiary policy, the line is resolved best-effort at capture and is usually
    /// null, and refusing a family because a label did not match a catalogue row would lose the
    /// prospect — the posture this module takes on every capture-path lookup. The completeness
    /// check reports a household that exceeds its line's ceiling afterwards, which is where an
    /// agent can act on it.
    /// </remarks>
    private static Task<Result<List<LeadHouseholdMember>>> AddHouseholdAsync(
        Lead lead, CaptureLeadCommand cmd, DateTimeOffset now)
    {
        var added = new List<LeadHouseholdMember>();

        foreach (var member in cmd.Household ?? [])
        {
            if (!Enum.TryParse<HouseholdMemberType>(member.Type, ignoreCase: true, out var type))
                return Task.FromResult(
                    Result.Fail<List<LeadHouseholdMember>>("INVALID_HOUSEHOLD_MEMBER_TYPE"));

            SpouseUnionType? unionType = null;

            if (!string.IsNullOrWhiteSpace(member.SpouseUnionType))
            {
                if (!Enum.TryParse<SpouseUnionType>(
                        member.SpouseUnionType, ignoreCase: true, out var parsed))
                    return Task.FromResult(
                        Result.Fail<List<LeadHouseholdMember>>("INVALID_SPOUSE_UNION_TYPE"));

                unionType = parsed;
            }

            Enum.TryParse<PersonGender>(member.Gender, ignoreCase: true, out var gender);

            PersonIdentity identity;
            try
            {
                identity = PersonIdentity.Create(
                    lastName:    member.LastName,
                    firstName:   member.FirstName,
                    birthDate:   member.BirthDate,
                    gender:      gender,
                    addressLine: member.AddressLine,
                    postalCode:  member.PostalCode,
                    city:        member.City,
                    countryCode: member.CountryCode);
            }
            catch (DomainException ex)
            {
                return Task.FromResult(
                    Result.Fail<List<LeadHouseholdMember>>(ex.MessageKey ?? "INVALID_IDENTITY"));
            }

            var result = lead.AddHouseholdMember(
                type:                 type,
                spouseUnionType:      unionType,
                identity:             identity,
                // Null, not NotExposed: nobody has been asked at capture, and a stamped
                // declaration would record an answer the form never collected.
                pep:                  null,
                usesPrincipalAddress: member.UsesPrincipalAddress,
                now:                  now);

            if (result.IsFailure)
                return Task.FromResult(Result.Fail<List<LeadHouseholdMember>>(result.Error!));

            added.Add(result.Value);
        }

        return Task.FromResult(Result.Ok(added));
    }

    /// <summary>
    /// Records the questionnaire answers, and the health consent each one needed.
    /// </summary>
    /// <remarks>
    /// The template is READ, not trusted: whether a questionnaire is a health declaration is the
    /// template's property, and the consent row is only written for the ones that are. The
    /// endpoint has already refused any answered questionnaire with no consent reference, so by
    /// the time a health template is found here the reference exists — which is why this method
    /// never has to choose between storing health answers and dropping them.
    ///
    /// <para>
    /// An unknown or unpublished template REFUSES the capture. A draft questionnaire's questions
    /// can still change, so answers recorded against one are answers to questions nobody can
    /// reconstruct — and silently dropping them would tell the caller the declaration was taken.
    /// </para>
    /// </remarks>
    private static async Task<Result> AddQuestionnairesAsync(
        LeadsDbContext db,
        Lead lead,
        CaptureLeadCommand cmd,
        List<LeadHouseholdMember> members,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var requested = cmd.Questionnaires ?? [];
        if (requested.Count == 0)
            return Result.Ok();

        var templateIds = requested.Select(q => q.TemplateId).Distinct().ToList();

        var templates = await db.QualificationTemplates
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Version, t.IsHealthQuestionnaire, t.Status })
            .ToListAsync(ct);

        var byId = templates.ToDictionary(t => t.Id);

        foreach (var questionnaire in requested)
        {
            if (!byId.TryGetValue(questionnaire.TemplateId, out var template))
                return Result.Fail(TemplateNotFound);

            if (template.Status != TemplateStatus.Published)
                return Result.Fail(TemplateNotPublished);

            // The principal, or the member at the index the request gave. The endpoint has already
            // refused an index outside the household it supplied.
            var (subjectType, subjectId) = questionnaire.SubjectIndex is { } index
                ? (QualificationSubjectType.HouseholdMember, members[index].Id)
                : (QualificationSubjectType.Principal, lead.Id);

            if (template.IsHealthQuestionnaire)
            {
                // The consent BEFORE the answers, and in the same transaction. BNF-B-09 makes an
                // evidenced consent the condition of the collection, so a declaration whose
                // consent row failed to write must not exist either.
                var consent = LeadConsent.Create(
                    tenantId:       lead.TenantId,
                    leadId:         lead.Id,
                    type:           ConsentType.HealthData,
                    // WebForm, because that is what a capture IS — the one channel every v2
                    // caller shares. An agent collecting on paper records the consent through the
                    // household endpoints, where they can say so.
                    channel:        ConsentChannel.WebForm,
                    recordedBy:     cmd.OwnerId ?? Guid.Empty,
                    clock:          TimeProvider.System,
                    proofReference: questionnaire.ConsentProofReference,
                    subjectType:    subjectType,
                    subjectId:      subjectId,
                    // Unknown at capture: a web form does not say who clicked. The principal is
                    // the only honest default for their own declaration, and for a child's the
                    // agent records the giver afterwards — which is why GivenBy is nullable.
                    givenBy:        null);

                db.LeadConsents.Add(consent);
            }

            var response = QualificationResponse.Create(
                tenantId:             lead.TenantId,
                leadId:               lead.Id,
                templateId:           questionnaire.TemplateId,
                answeredBy:           cmd.OwnerId ?? Guid.Empty,
                clock:                TimeProvider.System,
                answers:              [.. questionnaire.Answers.Select(a => (a.QuestionId, a.Value))],
                // Not scored at capture: the score belongs to the qualification, which runs on a
                // lead an agent has worked. Zero here is "not yet scored" and the qualification
                // recomputes it.
                computedScore:        0,
                questionnaireVersion: template.Version,
                subjectType:          subjectType,
                subjectId:            subjectId,
                isHealthData:         template.IsHealthQuestionnaire);

            db.QualificationResponses.Add(response);
        }

        return Result.Ok();
    }
}
