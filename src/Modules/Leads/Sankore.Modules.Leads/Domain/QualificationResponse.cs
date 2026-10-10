namespace Sankore.Modules.Leads.Domain;

using System.Text.Json;
using Sankore.Shared.Kernel;

/// <summary>
/// One person's answers to one qualification questionnaire (BNF-B-08).
///
/// <para>
/// <b>One response per PERSON, not per lead.</b> The same health questionnaire is answered for the
/// principal, the spouse and each child, so the identity of a response is
/// <c>(lead, template, subject type, subject id)</c> — enforced by
/// <c>ux_qualification_responses_subject</c>. A questionnaire scoped to the whole lead stores
/// <see cref="QualificationSubjectType.Lead"/> with the LEAD's own id as the subject, so that
/// constraint has a value to work with rather than a null PostgreSQL would treat as distinct from
/// every other null.
/// </para>
///
/// <para>
/// <b>Health answers are encrypted in bulk and never blind-indexed</b> (BNF-B-09). A blind index
/// exists so an encrypted column stays searchable by exact match; nothing searches for a person by
/// the answer they gave about their health, and an index would make "who answered yes to question
/// 7" a lookup. The audit trail records the question id only, never the value.
/// </para>
/// </summary>
public sealed class QualificationResponse : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid TemplateId { get; private set; }

    // ── Who it is about (BNF-B-08) ──────────────────────────────────────────

    public QualificationSubjectType SubjectType { get; private set; }

    /// <summary>
    /// The person this response is about: a household member id, the lead id for a
    /// <see cref="QualificationSubjectType.Lead"/> response, or the lead id again for
    /// <see cref="QualificationSubjectType.Principal"/> — the principal has no id of its own, it
    /// IS the lead. An opaque id, with no foreign key: the subject may live in another module's
    /// schema once BNF-B-23 lands.
    /// </summary>
    public Guid SubjectId { get; private set; }

    /// <summary>
    /// The person who actually answered, when that is not the subject (BNF-B-08).
    ///
    /// <para>
    /// Required for a MINOR and then constrained to the principal or the spouse: a four-year-old
    /// does not answer a health questionnaire, and recording the answers as though they had would
    /// misattribute a declaration that carries legal weight. Null when the subject answered for
    /// themselves.
    /// </para>
    /// </summary>
    public Guid? RespondentId { get; private set; }

    /// <summary>
    /// The template version these answers were given against (BNF-B-07).
    ///
    /// <para>
    /// FROZEN at creation, never read back from the template. Publishing a change bumps the
    /// template's version, and a response re-read through the live template would silently be
    /// re-interpreted against questions the person was never asked — which for a health
    /// declaration is the difference between a valid answer and a misrepresentation.
    /// </para>
    /// </summary>
    public int QuestionnaireVersion { get; private set; }

    public QualificationResponseStatus Status { get; private set; }

    // ── The answers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Whether these answers are health data (BNF-B-09) — copied from the template at creation.
    ///
    /// <para>
    /// A column rather than a join, because it decides how the row is READ: the purge job selects
    /// on it, the access check reads it, and the storage mapping encrypts on it. Resolving it
    /// through the template on every read would make each of those a join, and a template whose
    /// flag was later edited would retroactively change the classification of answers already
    /// given.
    /// </para>
    /// </summary>
    public bool IsHealthData { get; private set; }

    /// <summary>
    /// Non-health answers, plain <c>jsonb</c> in the column <c>answers</c> — exactly as this table
    /// has always stored them.
    /// </summary>
    private string PlainAnswersJson { get; set; } = "[]";

    /// <summary>
    /// Health answers, encrypted in bulk by the mapping into <c>health_answers_enc</c> (BNF-B-09).
    ///
    /// <para>
    /// <b>A SEPARATE column, not a converter on the one above.</b> A value converter sees only the
    /// value, never the row, so one column cannot be "jsonb unless this other field says
    /// otherwise". Two columns also make the split visible in psql — the <c>_enc</c> suffix says
    /// which rows hold ciphertext — and they make the dangerous direction impossible: a
    /// mis-classified response cannot put clear health answers into a jsonb column somebody later
    /// decides to index.
    /// </para>
    /// </summary>
    private string? HealthAnswersJson { get; set; }

    /// <summary>
    /// The answers, as a JSON array of <c>{ questionId, value }</c>, from whichever column holds
    /// them. Not mapped — see the two above.
    ///
    /// <para>
    /// Read it through <c>QualificationAnswerReader</c> and never directly, so the
    /// <c>lead:health:read</c> permission is applied in one place.
    /// </para>
    /// </summary>
    public string AnswersJson => IsHealthData ? HealthAnswersJson ?? "[]" : PlainAnswersJson;

    public Guid? AnsweredBy { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }
    public int ComputedScore { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// When the answers were handed to the insurance back-office — the moment the retention clock
    /// of BNF-B-09 starts. Null while they are still the only copy, which is why the purge cannot
    /// simply count from <see cref="CreatedAt"/>.
    /// </summary>
    public DateTimeOffset? TransmittedAt { get; private set; }

    /// <summary>True once the answers have been purged. The row survives; its content does not.</summary>
    public bool IsPurged { get; private set; }

    private QualificationResponse() { } // EF Core

    /// <summary>
    /// The expected-but-unanswered response the system creates when a product line asks a
    /// questionnaire of a person (BNF-B-08). A conversion refuses while one is still
    /// <see cref="QualificationResponseStatus.ToComplete"/>.
    /// </summary>
    public static QualificationResponse Expected(
        Guid tenantId,
        Guid leadId,
        Guid templateId,
        int questionnaireVersion,
        QualificationSubjectType subjectType,
        Guid subjectId,
        bool isHealthData,
        DateTimeOffset now)
        => new()
        {
            Id                   = Guid.NewGuid(),
            TenantId             = tenantId,
            LeadId               = leadId,
            TemplateId           = templateId,
            QuestionnaireVersion = questionnaireVersion,
            SubjectType          = subjectType,
            SubjectId            = subjectId,
            IsHealthData         = isHealthData,
            Status               = QualificationResponseStatus.ToComplete,
            CreatedAt            = now,
        };

    /// <summary>
    /// A response created already answered — the single-shot path <c>QualifyLeadHandler</c> has
    /// always used for a lead-scoped questionnaire.
    /// </summary>
    public static QualificationResponse Create(
        Guid tenantId,
        Guid leadId,
        Guid templateId,
        Guid answeredBy,
        TimeProvider clock,
        IReadOnlyList<(Guid QuestionId, string Value)> answers,
        int computedScore,
        int questionnaireVersion = 0,
        QualificationSubjectType subjectType = QualificationSubjectType.Lead,
        Guid? subjectId = null,
        bool isHealthData = false)
    {
        var now = clock.GetUtcNow();

        var response = new QualificationResponse
        {
            Id                   = Guid.NewGuid(),
            TenantId             = tenantId,
            LeadId               = leadId,
            TemplateId           = templateId,
            QuestionnaireVersion = questionnaireVersion,
            SubjectType          = subjectType,
            // Defaults to the LEAD's id rather than Guid.Empty: the unique constraint includes the
            // subject, and Guid.Empty across two leads would be two rows the index does not
            // separate — but Guid.Empty TWICE on one lead would collide, which is the correct
            // outcome and only works if the value is per-lead.
            SubjectId            = subjectId ?? leadId,
            IsHealthData         = isHealthData,
            Status               = QualificationResponseStatus.Completed,
            AnsweredBy           = answeredBy,
            AnsweredAt           = now,
            ComputedScore        = computedScore,
            CreatedAt            = now,
        };

        response.StoreAnswers(answers);
        return response;
    }

    /// <summary>
    /// Records the answers to an expected response.
    /// </summary>
    /// <param name="respondentId">
    /// Who answered, when that is not the subject. Validated by the handler against the principal
    /// and the spouse — the aggregate cannot see the household from here.
    /// </param>
    public Result Answer(
        IReadOnlyList<(Guid QuestionId, string Value)> answers,
        Guid answeredBy,
        Guid? respondentId,
        int computedScore,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(answers);

        if (IsPurged)
            return Result.Fail("QUALIFICATION_RESPONSE_PURGED");

        StoreAnswers(answers);
        AnsweredBy    = answeredBy;
        AnsweredAt    = now;
        RespondentId  = respondentId;
        ComputedScore = computedScore;
        Status        = QualificationResponseStatus.Completed;
        return Result.Ok();
    }

    /// <summary>
    /// Records that the answers reached the back-office — starting the retention clock (BNF-B-09).
    /// </summary>
    public void MarkTransmitted(DateTimeOffset now) => TransmittedAt ??= now;

    /// <summary>
    /// Blanks the answers, keeping the row (BNF-B-09, BNF-B-19). The row is what says a
    /// declaration was made and when; the content is what the retention period limits.
    /// </summary>
    public void Purge()
    {
        PlainAnswersJson  = "[]";
        HealthAnswersJson = null;
        IsPurged          = true;
    }

    /// <summary>
    /// Deserializes the stored answers.
    /// </summary>
    /// <remarks>
    /// <b>Do not call this on a health response.</b> Go through <c>QualificationAnswerReader</c>,
    /// which applies the <c>lead:health:read</c> permission. This method is the raw accessor and
    /// an architecture test keeps the health path from reaching anything that is not that reader.
    /// </remarks>
    public IReadOnlyList<(Guid QuestionId, string Value)> GetAnswers()
    {
        var raw = JsonSerializer.Deserialize<AnswerPayload[]>(AnswersJson) ?? [];
        return raw.Select(a => (a.questionId, a.value)).ToList();
    }

    /// <summary>
    /// Routes the answers to the column their classification belongs in — the ONE place that
    /// decides, so a health response can never land in the plain jsonb column.
    /// </summary>
    private void StoreAnswers(IReadOnlyList<(Guid QuestionId, string Value)> answers)
    {
        var json = JsonSerializer.Serialize(
            answers.Select(a => new { questionId = a.QuestionId, value = a.Value }).ToList());

        if (IsHealthData)
        {
            HealthAnswersJson = json;
            PlainAnswersJson  = "[]";
        }
        else
        {
            PlainAnswersJson  = json;
            HealthAnswersJson = null;
        }
    }

    // ── private JSON shape ────────────────────────────────────────────────
#pragma warning disable IDE1006
    private sealed record AnswerPayload(Guid questionId, string value);
#pragma warning restore IDE1006
}
