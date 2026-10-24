namespace Sankore.Modules.Leads.Features.Enrichment.EnrichLead;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Completes a dispatched lead's file (BNF-B-02/B-04): the principal's identity, and the product
/// line the request is about.
///
/// <para>
/// <b>Every field is optional and null means "leave alone".</b> Enrichment happens over several
/// conversations — an address today, a birth date next week — from a screen that posts only what
/// it collected. A null that cleared would make each save erase the last. Blank clears; see
/// <c>Lead.EnrichPrincipalIdentity</c>.
/// </para>
///
/// <para>
/// <b>What it deliberately does NOT carry.</b> The PEP status has its own command
/// (<c>SetPrincipalPepStatusCommand</c>) because declaring a <c>Self</c> exposure reclassifies the
/// whole household and the audit row has to say so. The e-mail has one setter,
/// <c>Lead.SetEmail</c>, which maintains the blind index. The household, the questionnaires and
/// the FATCA declaration are each a slice with a consent or a compliance rule this command would
/// have to duplicate. This is the identity-and-product command, not a general lead writer.
/// </para>
///
/// <para>
/// It answers with the recomputed <c>LeadCompletenessDto</c>, so the screen that saved a field
/// learns what is still missing without a second round trip — and learns it from the same
/// calculator the conversion will use.
/// </para>
/// </summary>
/// <param name="SourceActivityId">
/// The <c>LeadActivity</c> during which this was collected, when an agent is enriching from a call
/// or a visit (BNF-B-02). Validated to belong to this lead. It
/// is kept on the lead as <c>IdentitySourceActivityId</c>, so the identity screen can say which
/// call or visit confirmed what the prospect declared, and carried on <c>LeadEnriched</c>.
/// </param>
/// <param name="NameVerified">
/// The agent confirms the spelling of the name (<c>Lead.NameVerifiedAt</c>). A flag on this command
/// rather than a command of its own: it is the same screen, the same access and staleness gates,
/// and usually the same save as the name it confirms.
/// </param>
internal sealed record EnrichLeadCommand(
    Guid LeadId,
    string? FirstName = null,
    string? LastName = null,
    string? UsageName = null,
    DateOnly? BirthDate = null,
    string? Gender = null,
    string? AddressLine = null,
    string? PostalCode = null,
    string? City = null,
    string? CountryCode = null,
    Guid? SourceActivityId = null,
    DateTimeOffset? ExpectedUpdatedAt = null,
    string? Neighbourhood = null,
    string? Landmark = null,
    bool NameVerified = false
) : IRequest<Result<LeadCompletenessDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
