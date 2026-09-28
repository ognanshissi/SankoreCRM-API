namespace Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;

using MediatR;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Registers an individual client (US-M01-BE-04) with its blocking / warning duplicate
/// controls (US-M01-BE-05).
///
/// Every value that ends up encrypted at rest is marked <see cref="SensitiveDataAttribute"/>
/// so <c>AuditBehavior</c> writes <c>"***"</c> instead of the value: the audit trail must
/// record WHO created WHICH client, never the document number they typed.
///
/// <see cref="IAgencyScopedRequest.TargetAgencyId"/> lets
/// <c>AgencyAuthorizationBehavior</c> reject an out-of-perimeter agency with
/// <c>AGENCY_OUT_OF_SCOPE</c> before the handler runs — the agency is a command field
/// here, so the check does not need to load the client first.
/// </summary>
/// <param name="ConfirmNoDuplicate">
/// Operator's explicit override of the phone warning. It NEVER overrides the identity
/// document block: a shared family phone is plausible, two people holding the same
/// national id card is not.
/// </param>
public sealed record CreateIndividualClientCommand(
    Guid AgencyId,
    Guid? AdvisorUserId,
    string FirstName,
    string LastName,
    string? MaidenName,
    Gender Gender,
    [property: SensitiveData] DateOnly DateOfBirth,
    string? BirthPlace,
    string Nationality,
    MaritalStatus? MaritalStatus,
    string? FatherName,
    string? MotherName,
    string? Profession,
    string? Employer,
    [property: SensitiveData] decimal? DeclaredIncome,
    string? DeclaredIncomeCurrency,
    string? PreferredLanguage,
    IdentityDocumentType IdentityDocumentType,
    [property: SensitiveData] string IdentityDocumentNumber,
    DateOnly? IdentityDocumentIssuedOn,
    DateOnly? IdentityDocumentExpiresOn,
    [property: SensitiveData] IReadOnlyList<string> PhoneNumbers,
    [property: SensitiveData] string? Email,
    [property: SensitiveData] PostalAddressInput? Address,
    bool ConfirmNoDuplicate
) : IRequest<Result<CreateClientResult>>, ICommand, IResourceCommand, IAgencyScopedRequest
{
    public string ResourceType => "Client";

    /// <summary>Null: the id does not exist yet at dispatch time.</summary>
    public string? ResourceId => null;

    public Guid? TargetAgencyId => AgencyId;
}

/// <summary>
/// Why the creation ended the way it did. Modelled as an OUTCOME on a successful
/// <c>Result</c> rather than as an error code, because the two duplicate cases must
/// carry a payload (the candidate clients) that a <c>Result.Fail(string)</c> cannot hold.
/// The endpoint maps the two non-<see cref="Created"/> outcomes to HTTP 409.
/// </summary>
public enum CreateClientOutcome
{
    Created,
    BlockedDuplicateIdentityDocument,
    WarningPossibleDuplicatePhone
}

/// <param name="Code">
/// The <see cref="CustomerErrors"/> code matching a non-<c>Created</c> outcome, so the
/// front-end localises the message; null on success.
/// </param>
/// <param name="Candidates">
/// The already-existing clients that triggered the block or the warning. Non-sensitive
/// by construction (see <see cref="DuplicateHit"/>).
/// </param>
public sealed record CreateClientResult(
    CreateClientOutcome Outcome,
    Guid? ClientId,
    string? ClientNumber,
    string? Code,
    IReadOnlyList<DuplicateHit> Candidates);
