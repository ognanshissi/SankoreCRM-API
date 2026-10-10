namespace Sankore.Modules.Leads.Features.Questionnaires;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Decrypted health questionnaire answers (BNF-B-09).
///
/// <para>
/// <b>This type exists to be hard to reach.</b> It is the only shape in which health answers leave
/// the persistence layer in clear, and <c>ScoringIsolationTests</c> asserts that nothing outside
/// this feature slice references it — not the scorer, not the dispatcher. A plain
/// <c>IReadOnlyList&lt;(Guid, string)&gt;</c> would have been indistinguishable from any other
/// answer list, so no architecture rule could have named it.
/// </para>
///
/// <para>
/// Obtained ONLY from <see cref="HealthAnswerAccess.Read"/>, which is where the
/// <c>lead:health:read</c> permission is applied.
/// </para>
/// </summary>
public sealed record DecryptedHealthAnswers(
    Guid ResponseId,
    QualificationSubjectType SubjectType,
    Guid SubjectId,
    int QuestionnaireVersion,
    IReadOnlyList<HealthAnswer> Answers);

/// <summary>One answer to a health question. The VALUE — handle accordingly.</summary>
public sealed record HealthAnswer(Guid QuestionId, string Value);

/// <summary>
/// The single door to health answers in clear (BNF-B-09).
///
/// <para>
/// A static gate rather than a service, because there is nothing to inject: the decision is the
/// caller's permission and the row's own classification. Keeping it in one function is what makes
/// "who can read a health declaration" answerable by reading one file.
/// </para>
/// </summary>
public static class HealthAnswerAccess
{
    internal const string Forbidden = "HEALTH_ANSWERS_FORBIDDEN";
    internal const string Purged = "QUALIFICATION_RESPONSE_PURGED";

    /// <summary>
    /// Decrypts and returns the answers, or refuses.
    /// </summary>
    /// <param name="callerHoldsHealthPermission">
    /// Whether the caller holds <c>lead:health:read</c>. Decided by the ENDPOINT from the caller's
    /// claims — never worked out here, so an authorization reviewer finds the check where they
    /// look for it.
    /// </param>
    /// <remarks>
    /// A NON-health response is returned without the permission: the gate is about health data, not
    /// about questionnaires. A BANT answer is ordinary lead information.
    /// </remarks>
    public static Result<DecryptedHealthAnswers> Read(
        QualificationResponse response, bool callerHoldsHealthPermission)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsHealthData && !callerHoldsHealthPermission)
            return Result.Fail<DecryptedHealthAnswers>(Forbidden);

        // Distinct from "no answers": the retention period expired and the content is gone, which
        // a caller must be able to tell from a questionnaire nobody filled in.
        if (response.IsPurged)
            return Result.Fail<DecryptedHealthAnswers>(Purged);

        return Result.Ok(new DecryptedHealthAnswers(
            ResponseId:           response.Id,
            SubjectType:          response.SubjectType,
            SubjectId:            response.SubjectId,
            QuestionnaireVersion: response.QuestionnaireVersion,
            Answers:              [.. response.GetAnswers().Select(a => new HealthAnswer(a.QuestionId, a.Value))]));
    }
}
