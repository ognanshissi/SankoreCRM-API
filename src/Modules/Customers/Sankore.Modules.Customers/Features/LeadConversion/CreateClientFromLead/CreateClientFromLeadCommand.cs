namespace Sankore.Modules.Customers.Features.LeadConversion.CreateClientFromLead;

using System.Globalization;
using MediatR;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// US-M01-BE-06 — turns a lead converted by module M13 into a client of module M01.
///
/// <para>
/// There is NO HTTP endpoint for this slice, and there is no MassTransit consumer either:
/// <c>LeadConvertedIntegrationEvent</c> is declared inside the Leads MAIN assembly
/// (<c>Sankore.Modules.Leads.Features.ConvertLead.Events</c>), which module isolation
/// forbids us to reference. The only entry point is therefore the synchronous
/// <see cref="ICustomersModule.CreateFromLeadAsync"/> call, dispatched here through
/// <c>ILeadConversionService</c> — see the comment at the top of the handler.
/// </para>
///
/// <para>
/// <b>Why the request is flattened instead of being carried as a nested
/// <see cref="CreateFromLeadRequest"/>.</b> The audit trail serializes every
/// <see cref="ICommand"/> with <c>SanitizedJsonSerializer</c>, which redacts a property
/// only when that property itself carries <see cref="SensitiveDataAttribute"/>.
/// <see cref="CreateFromLeadRequest"/> is a PublicApi contract owned by another slice and
/// carries no such attribute, so nesting it would write the lead's phone number, e-mail,
/// document number and date of birth to the audit log in clear text. Copying the fields
/// onto this command is what lets us mark them — §10 of the slice spec asked for a nested
/// record, the Definition of Done asked for the attributes; the attributes win, because one
/// is a naming preference and the other is a data-protection requirement.
/// </para>
///
/// <para>
/// <see cref="TenantId"/> travels IN the command rather than being read from
/// <c>ICurrentUser</c>/<c>ITenantContext</c>: the caller is another module, possibly running
/// in a Hangfire job or a message consumer where no ambient tenant exists. Every query in the
/// handler consequently runs with <c>IgnoreQueryFilters()</c> plus an explicit
/// <c>TenantId ==</c> predicate — that predicate is the only thing keeping tenants apart on
/// this code path.
/// </para>
/// </summary>
/// <param name="DateOfBirth">
/// ISO-8601 (<c>yyyy-MM-dd</c>) rather than <see cref="DateOnly"/>, and that is not a style
/// choice: <c>SanitizedJsonSerializer</c> redacts by replacing the property's getter with the
/// literal <c>"***"</c>, so a <see cref="SensitiveDataAttribute"/> on any property that is not a
/// <see cref="string"/> makes the audit serializer throw <see cref="InvalidCastException"/> the
/// first time that command is audited. A date of birth is protected data (it is encrypted at
/// rest and sits behind the audited reveal endpoint), so the choice is between a string and a
/// clear-text birth date in the audit log. Use <see cref="BirthDate"/> to read it back.
/// </param>
/// <param name="RequestedClientId">
/// The identifier module M13 already stamped on the lead (<c>Lead.CustomerId</c>). Reusing it
/// keeps the two records pointing at each other; when null, the handler assigns a fresh one.
/// </param>
internal sealed record CreateClientFromLeadCommand(
    Guid TenantId,
    Guid LeadId,
    Guid AgencyId,
    Guid ConvertedByUserId,
    string? FirstName,
    string? LastName,
    string? LegalName,
    string? Gender,
    [property: SensitiveData] string? DateOfBirth,
    string? Nationality,
    [property: SensitiveData] string? PhoneNumber,
    [property: SensitiveData] string? Email,
    string? IdentityDocumentType,
    [property: SensitiveData] string? IdentityDocumentNumber,
    string? Profession,
    string? PreferredLanguage,
    Guid? RequestedClientId
) : IRequest<Result<CreateFromLeadResult>>, ICommand, IResourceCommand
{
    /// <summary>ISO-8601 date format used by <see cref="DateOfBirth"/>.</summary>
    internal const string DateOfBirthFormat = "yyyy-MM-dd";

    public string ResourceType => "Client";

    /// <summary>Null: the client identifier is only known once the handler has run.</summary>
    public string? ResourceId => null;

    /// <summary>
    /// <see cref="DateOfBirth"/> parsed, or null when absent or malformed (the validator rejects
    /// the malformed case before the handler runs). <c>internal</c> on purpose: a public computed
    /// property would be picked up by the audit serializer and would hand back in clear exactly
    /// what <see cref="DateOfBirth"/> redacts.
    /// </summary>
    internal DateOnly? BirthDate
        => DateOnly.TryParseExact(
            DateOfBirth, DateOfBirthFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;

    /// <summary>Copies the PublicApi request onto the audited command shape.</summary>
    internal static CreateClientFromLeadCommand From(CreateFromLeadRequest request) => new(
        TenantId: request.TenantId,
        LeadId: request.LeadId,
        AgencyId: request.AgencyId,
        ConvertedByUserId: request.ConvertedByUserId,
        FirstName: request.FirstName,
        LastName: request.LastName,
        LegalName: request.LegalName,
        Gender: request.Gender,
        DateOfBirth: request.DateOfBirth?.ToString(DateOfBirthFormat, CultureInfo.InvariantCulture),
        Nationality: request.Nationality,
        PhoneNumber: request.PhoneNumber,
        Email: request.Email,
        IdentityDocumentType: request.IdentityDocumentType,
        IdentityDocumentNumber: request.IdentityDocumentNumber,
        Profession: request.Profession,
        PreferredLanguage: request.PreferredLanguage,
        RequestedClientId: request.RequestedClientId);
}

/// <summary>
/// Codes this slice can return on top of <see cref="Domain.CustomerErrors"/>.
/// </summary>
internal static class LeadConversionErrorCodes
{
    /// <summary>
    /// The lead carries neither a first/last name pair nor a legal name, so module M01
    /// cannot tell whether to open an individual or a legal-entity record.
    /// </summary>
    internal const string LeadIdentityIncomplete = "LEAD_IDENTITY_INCOMPLETE";
}
