namespace Sankore.Modules.Leads.Features.CaptureLead;

using Sankore.Shared.Kernel;

/// <summary>
/// Health answers may not arrive at capture without the evidence of a consent (BNF-B-09, B-18).
///
/// <para>
/// <b>Why it runs on the REQUEST and before anything is written.</b> The alternative shapes are
/// both worse. Storing the answers and ignoring the missing consent is what BNF-B-09 exists to
/// forbid. Silently dropping the questionnaire while returning 201 is worse still: the caller
/// believes a health declaration was recorded, the insurer later receives a file without it, and
/// nothing in the system ever says why.
/// </para>
///
/// <para>
/// <b>Why the check is here and not only in the handler.</b> The handler cannot tell a health
/// questionnaire from an ordinary one without reading the template — a database call — and it
/// does, for the ones it accepts. What this does is cheaper and stricter: a questionnaire with
/// ANSWERS and no consent reference is refused whatever its template says, because a caller that
/// has consent evidence has no reason to omit the reference, and one that does not must not be
/// able to find out whether a given template happens to be flagged as health.
/// </para>
///
/// <para>
/// That last point is the real reason, and it is a deliberate trade: a few non-health
/// questionnaires will be refused for want of a reference nobody needed. The cost is a 422 an
/// integrator fixes once; the alternative leaks which templates are health questionnaires to an
/// unauthenticated web form, and lets a caller probe for them by submitting answers.
/// </para>
/// </summary>
internal static class CaptureHealthConsentCheck
{
    internal const string ConsentProofRequired = "HEALTH_CONSENT_PROOF_REQUIRED";
    internal const string SubjectOutOfRange = "QUESTIONNAIRE_SUBJECT_OUT_OF_RANGE";

    /// <summary>
    /// Ok when every questionnaire that carries answers also carries a consent reference, and
    /// every subject index points at a member the request actually supplies.
    /// </summary>
    internal static Result Validate(
        IReadOnlyList<CaptureQuestionnaire>? questionnaires,
        int householdCount = 0)
    {
        if (questionnaires is null or { Count: 0 })
            return Result.Ok();

        foreach (var questionnaire in questionnaires)
        {
            // An empty questionnaire is not a declaration — it is a caller saying "this one is
            // expected and not yet answered", which the planner creates on its own. Nothing
            // sensitive travels, so nothing is required.
            if (questionnaire.Answers is null or { Count: 0 })
                continue;

            if (string.IsNullOrWhiteSpace(questionnaire.ConsentProofReference))
                return Result.Fail(ConsentProofRequired);

            // A subject index pointing past the household is a caller error, and accepting it
            // would attach a declaration to the principal silently — attributing one person's
            // health answers to another.
            if (questionnaire.SubjectIndex is { } index
                && (index < 0 || index >= householdCount))
                return Result.Fail(SubjectOutOfRange);
        }

        return Result.Ok();
    }
}
