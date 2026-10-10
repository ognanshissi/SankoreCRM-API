namespace Sankore.Modules.Integration.Features.Commands;

using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Kyc.PublicApi;

/// <summary>
/// Assembles the <see cref="CbsCustomerPayload"/> a customer-creation command carries.
///
/// <para>
/// It runs twice, and both are deliberate. When the command is CREATED the result is encrypted
/// onto the row, so the write owed is recorded with the data it was decided on and survives
/// whatever the external system is doing. When the dispatcher SENDS it, the payload is re-derived
/// from current CRM state and written back — because the CRM is the source of truth, and a
/// command rejected over a wrong value is fixed by correcting the client record and replaying.
/// That re-derivation is also the ONLY path allowed to replace a payload: an operator-supplied
/// one would be a write whose content the audit row cannot show, since INT-08 keeps payload
/// values out of it by design.
/// </para>
///
/// <para>
/// <b>What it cannot fill, and why that is not a defect of this class.</b> Only M01 holds a
/// client's document number, postal address and declared income, and
/// <see cref="ICustomersModule"/> states that no method of it ever returns decrypted sensitive
/// data — a caller that needs those must go through M01's audited reveal endpoint. This module is
/// a caller like any other. The fields therefore stay <c>null</c> here, the payload column exists
/// and is encrypted precisely so it can carry them the day INT-14 wires that reveal channel, and
/// an adapter that requires one answers <see cref="IntegrationErrors.PayloadInvalid"/> — a
/// Technical rejection naming the command, visible in the rejection queue, rather than a
/// customer silently created at the CBS with half an identity.
/// </para>
/// </summary>
internal sealed class CbsCustomerPayloadSource(ICustomersModule customers, IKycModule kyc)
{
    /// <summary>
    /// The payload, or <c>null</c> when M01 does not know the id in that tenant — a dangling
    /// reference. The caller turns that into a refusal rather than queueing a creation for a
    /// customer that does not exist.
    /// </summary>
    internal async Task<CbsCustomerPayload?> BuildAsync(
        Guid tenantId, Guid crmCustomerId, CancellationToken ct)
    {
        var client = await customers.GetClientSummaryAsync(tenantId, crmCustomerId, ct);
        if (client is null) return null;

        // Phone and e-mail come from the other projection of the same contract, which does return
        // them. Two calls rather than one because neither projection is a superset of the other.
        var contact = await customers.GetCustomerAsync(tenantId, crmCustomerId, ct);

        var isLegalEntity = string.Equals(client.ClientType, "Legal", StringComparison.OrdinalIgnoreCase);

        return new CbsCustomerPayload(
            CrmCustomerId: crmCustomerId,

            // M01 composes one DisplayName and its public contract exposes no split, so the name
            // is carried WHOLE in the field the CBS keys its party search on rather than guessed
            // into two: splitting on whitespace corrupts every compound surname, which in this
            // region is the common case and not the exception.
            FirstName: null,
            LastName: isLegalEntity ? null : client.DisplayName,
            LegalName: isLegalEntity ? client.DisplayName : null,

            // Encrypted in M01 and not exposed by its contract. See the class comment.
            DateOfBirth: null,
            Gender: null,
            MaritalStatus: null,
            Nationality: null,
            IdDocumentType: null,
            IdDocumentNumber: null,

            PhoneNumber: contact?.PhoneNumber,
            Email: contact?.Email,

            AddressLine: null,
            City: null,
            Country: null,
            Profession: null,
            Sector: null,

            // CRM codes, translated by the adapter through integration_mapping. The agency id IS
            // the CRM code for the Agency domain: nothing else identifies an agency across
            // modules, and a mapping row is what turns it into the CBS's own branch code.
            AgencyCode: client.AgencyId == Guid.Empty ? null : client.AgencyId.ToString(),

            KycLevel: await ResolveKycLevelAsync(tenantId, crmCustomerId, ct),

            // The client number, so an operator looking at the CBS can find the SANKORE record.
            CrmReference: client.ClientNumber);
    }

    /// <summary>
    /// The tier as M02 computes it, not as this module guesses it.
    ///
    /// <para>
    /// <c>GetLimitsAsync</c> already answers "no file, or a file that grants nothing" with
    /// <c>null</c>, and caps an expired full file back to Simplified. Reading its answer rather
    /// than the raw status keeps one definition of a tier in the platform — and the fail-closed
    /// direction here is <see cref="KycLevel.None"/>, which the CBS reads as "no tier yet".
    /// </para>
    /// </summary>
    private async Task<KycLevel> ResolveKycLevelAsync(
        Guid tenantId, Guid crmCustomerId, CancellationToken ct)
    {
        var limits = await kyc.GetLimitsAsync(tenantId, crmCustomerId, ct);

        if (limits is null) return KycLevel.None;

        return string.Equals(limits.Tier, "Full", StringComparison.OrdinalIgnoreCase)
            ? KycLevel.Full
            : KycLevel.Simplified;
    }
}
