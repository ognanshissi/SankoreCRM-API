namespace Sankore.Modules.Customers.Features.Compliance.AmlProfile;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records what the institution established for a client's LBC/FT file: the sector of activity,
/// the origin of the funds, and — for a legal person — the stated object and the issue date of the
/// registry extract on file.
///
/// <para>
/// In the Compliance area rather than next to <c>UpdateClient</c>, and that is the point of the
/// placement: these are not profile fields an agent tidies up, they are the answers an institution
/// owes a supervisor. <see cref="FundsOriginDetail"/> is marked sensitive — it describes a named
/// person's assets, so the audit trail records the field NAME and never the value.
/// </para>
/// </summary>
internal sealed record UpdateAmlProfileCommand(
    Guid ClientId,
    uint ExpectedVersion,
    string? ActivitySectorCode,
    string? ActivitySectorDetail,
    string? FundsOriginCode,
    [property: SensitiveData] string? FundsOriginDetail,
    string? CorporatePurpose,
    DateOnly? RegistrationExtractIssuedOn
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>The client id travels in the route.</summary>
internal sealed record UpdateAmlProfileRequest(
    uint ExpectedVersion,
    string? ActivitySectorCode = null,
    string? ActivitySectorDetail = null,
    string? FundsOriginCode = null,
    string? FundsOriginDetail = null,
    string? CorporatePurpose = null,
    DateOnly? RegistrationExtractIssuedOn = null);
