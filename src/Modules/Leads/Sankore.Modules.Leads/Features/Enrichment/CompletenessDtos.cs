namespace Sankore.Modules.Leads.Features.Enrichment;

using Sankore.Modules.Leads.Features;

/// <summary>
/// One thing still missing (BNF-B-04).
/// </summary>
/// <param name="Code">One of <see cref="CompletenessCodes"/>. Stable; branch on this.</param>
/// <param name="SubjectLabel">
/// Who it is missing for, in words — "Le titulaire", "L'enfant Kofi". Null for a lead-wide item.
/// BNF-B-04 wants the list PER PERSON, and an id is not that.
/// </param>
/// <param name="Detail">
/// What specifically — a field code, a questionnaire name. Never a value, and never a sentence
/// the client is expected to display verbatim.
/// </param>
/// <param name="ActionHint">
/// Where the agent has to go, for an item that cannot be fixed on the lead (BNF-B-23: "un manque
/// bloquant pointe vers l'action à mener sur la fiche client"). Null when the lead itself is the
/// place.
/// </param>
/// <param name="Blocking">
/// Whether this gap alone refuses the conversion. Almost all of them do — a list called
/// "required fields BEFORE conversion" is a gate by definition. The exception is FATCA
/// documentation outstanding: the indices are declared and the institution is waiting on the
/// client's form, which is a compliance queue item (<c>RequiresComplianceReview</c>), not a reason
/// to stop a subscription that the regulator itself expects to proceed. It still counts against
/// the percentage, because the file genuinely is incomplete.
/// </param>
public sealed record CompletenessGap(
    string Code,
    string? SubjectLabel,
    string? Detail,
    string? ActionHint = null,
    bool Blocking = true);

/// <summary>
/// How complete a lead's file is (BNF-B-04).
/// </summary>
/// <param name="Percentage">
/// 0–100, over the checks that APPLY to this lead. A lead whose tenant has FATCA off is not
/// penalised for having no FATCA declaration — BNF-B-11 says it is not even computed.
/// </param>
/// <param name="CanConvert">
/// Whether the conversion gate would pass. Not simply <c>Gaps.Count == 0</c>: some gaps are
/// informational, and this is the single answer a screen should disable its button on.
/// </param>
/// <param name="Kind">"NewProspect" | "ExistingCustomer" — which rules were applied.</param>
/// <param name="RequiredConsents">
/// The <c>ConsentType</c> names the product line requires before conversion, given or not. The
/// gaps only name the MISSING ones, and a consent screen has to keep saying « obligatoire » once
/// it is given — otherwise a required consent would read as optional the moment it is collected.
/// </param>
public sealed record LeadCompletenessDto(
    Guid LeadId,
    string Kind,
    int Percentage,
    int Satisfied,
    int Applicable,
    bool CanConvert,
    IReadOnlyList<CompletenessGap> Gaps,
    IReadOnlyList<string>? RequiredConsents = null);
