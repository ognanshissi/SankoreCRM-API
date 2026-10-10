namespace Sankore.Modules.Leads.Features.Fatca.GetDeclarations;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>The FATCA position of every person of a lead (BNF-B-11).</summary>
internal sealed record GetFatcaDeclarationsQuery(Guid LeadId)
    : IRequest<Result<LeadFatcaDto>>;

/// <summary>
/// One declaration.
/// </summary>
/// <param name="Status">
/// The EFFECTIVE status — <c>NotApplicable</c> when the tenant is not subject to FATCA, whatever
/// the stored value. Turning FATCA off does not rewrite declarations; it changes what they mean.
/// </param>
/// <param name="DeclaredIndicia">
/// The indices that were ticked, by NAME. Each stays separately identifiable because the evidence
/// that cures an index depends on which index it is.
/// </param>
/// <remarks>
/// Carries no US TIN and no document reference. The TIN is a reportable identifier with no reason
/// to appear on a screen that lists statuses, and a document is fetched through the file store by
/// somebody who has a reason to open it.
/// </remarks>
public sealed record FatcaDeclarationDto(
    Guid DeclarationId,
    string SubjectType,
    Guid SubjectId,
    string SubjectLabel,
    string Status,
    string Documentation,
    IReadOnlyList<string> DeclaredIndicia,
    bool HasUsTin,
    bool HasSelfCertification,
    bool HasEvidence,
    DateTimeOffset? SignedAt,
    bool RequiresComplianceReview,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SupersededAt);

/// <param name="Enabled">
/// False when the tenant is not subject to FATCA. The screen needs it to hide the whole section
/// rather than show empty fields the agent would try to fill (BNF-B-11: "ni exposé").
/// </param>
/// <param name="Missing">
/// People in scope with no active declaration. Empty when FATCA is off — "ni calculé dans la
/// complétude".
/// </param>
public sealed record LeadFatcaDto(
    Guid LeadId,
    bool Enabled,
    bool RequiredAtEnrichment,
    IReadOnlyList<FatcaDeclarationDto> Declarations,
    IReadOnlyList<MissingFatcaDeclaration> Missing,
    bool IsComplete);

/// <param name="Reason">
/// <c>FATCA_DECLARATION_MISSING</c> when nobody has declared, or
/// <c>FATCA_DOCUMENTATION_OUTSTANDING</c> when indices were declared and nothing was produced —
/// the agent has to know which of the two it is to know what to do next.
/// </param>
public sealed record MissingFatcaDeclaration(
    string SubjectType,
    Guid SubjectId,
    string SubjectLabel,
    string Reason);
