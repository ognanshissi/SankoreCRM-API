namespace Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;

using MediatR;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// US-M01-BE-17 — registers a legal entity (personne morale).
/// <para>
/// The registration number (RCCM) and the tax id (NIF) are protected data: they are
/// encrypted before they ever reach the aggregate, and only the RCCM carries a blind
/// index so the duplicate check below is a plain indexed equality — never a decryption.
/// </para>
/// </summary>
public sealed record CreateLegalClientCommand(
    Guid AgencyId,
    Guid? AdvisorUserId,
    string LegalName,
    string LegalFormCode,
    [property: SensitiveData] string RegistrationNumber,
    [property: SensitiveData] string? TaxIdNumber,
    DateOnly IncorporationDate,
    [property: SensitiveData] PostalAddressInput HeadOfficeAddress,
    [property: SensitiveData] IReadOnlyList<string> PhoneNumbers,
    [property: SensitiveData] string? Email,
    string? PreferredLanguage
) : IRequest<Result<CreateLegalClientResult>>, ICommand, IResourceCommand, IAgencyScopedRequest
{
    public string ResourceType => "Client";

    /// <summary>Null: the identifier is assigned by the handler.</summary>
    public string? ResourceId => null;

    /// <summary>
    /// Lets <c>AgencyAuthorizationBehavior</c> reject the request with
    /// <c>AGENCY_OUT_OF_SCOPE</c> before the handler runs.
    /// </summary>
    public Guid? TargetAgencyId => AgencyId;
}

/// <summary>
/// Same three-way outcome shape as the individual creation slice (§1.2): a blocking
/// duplicate is a SUCCESSFUL result carrying a code, so the endpoint can answer 409
/// together with the offending client — a <c>Result.Fail</c> could not carry it.
/// </summary>
public enum CreateLegalClientOutcome
{
    Created,
    BlockedDuplicateRegistrationNumber
}

/// <param name="Code">
/// <c>null</c> when <paramref name="Outcome"/> is <see cref="CreateLegalClientOutcome.Created"/>,
/// otherwise the error code the front-end localizes (<c>DUPLICATE_REGISTRATION_NUMBER</c>).
/// </param>
public sealed record CreateLegalClientResult(
    CreateLegalClientOutcome Outcome,
    Guid? ClientId,
    string? ClientNumber,
    string? Code,
    IReadOnlyList<DuplicateHit> Candidates);
