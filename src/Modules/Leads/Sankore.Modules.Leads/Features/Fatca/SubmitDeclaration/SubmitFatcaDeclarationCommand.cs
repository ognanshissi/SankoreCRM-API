namespace Sankore.Modules.Leads.Features.Fatca.SubmitDeclaration;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records a person's FATCA self-certification (BNF-B-11). A change of circumstances is a NEW
/// submission: the previous declaration is superseded, never edited.
/// </summary>
/// <param name="SubjectType">"Principal" | "HouseholdMember".</param>
/// <param name="Documentation">"None" | "FormW9" | "FormW8Ben" | "Refused".</param>
/// <param name="UsTin">
/// Required for <c>FormW9</c>. Travels in clear and is encrypted by the mapping; it appears in no
/// event, no audit row and no response.
/// </param>
/// <param name="SignedAt">
/// When the person signed the self-certification — not when this request was made (F02.10).
/// Required unless the documentation is a refusal.
/// </param>
internal sealed record SubmitFatcaDeclarationCommand(
    Guid LeadId,
    string SubjectType,
    Guid SubjectId,
    FatcaIndiciaInput Indicia,
    string Documentation,
    string? UsTin = null,
    string? SelfCertificationDocRef = null,
    string? EvidenceDocRef = null,
    DateTimeOffset? SignedAt = null,
    string? RefusalReason = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

/// <summary>
/// The seven indices as the API accepts them (BNF-B-11).
/// </summary>
/// <remarks>
/// A flat DTO rather than the domain record, for the same reason as the household identity: the
/// domain record is reached through a factory, and this is the shape the generated Angular client
/// binds to.
/// </remarks>
public sealed record FatcaIndiciaInput(
    bool UsNationalityOrResidence = false,
    bool BornInTheUnitedStates = false,
    bool UsAddressOrPoBox = false,
    bool UsPhoneNumber = false,
    bool StandingOrderToUsAccount = false,
    bool PowerOfAttorneyWithUsAddress = false,
    bool CareOfOrPosteRestanteAsSoleAddress = false);
