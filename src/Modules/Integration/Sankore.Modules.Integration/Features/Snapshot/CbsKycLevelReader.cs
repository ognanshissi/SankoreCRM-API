namespace Sankore.Modules.Integration.Features.Snapshot;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The KYC tier the CBS holds, for criterion 1 of INT-21 — read from the writes the CBS
/// ACKNOWLEDGED, because no port exposes it.
///
/// <para>
/// <b>This is the one figure of the snapshot that does not come from a read port.</b>
/// <c>ICbsCustomerPort</c> can <c>SetKycLevelAsync</c> but offers nothing to get it back, and that
/// contract is fixed. So the authoritative statement available to us is the last tier a command
/// reached <c>Succeeded</c> with: the CBS confirmed that write, so that is the tier it held at
/// that moment, and <c>IntegrationCommand.PayloadEncrypted</c> is where the tier we sent is kept.
/// </para>
///
/// <para>
/// What this can and cannot see is worth being explicit about, because criterion 4 is built on it.
/// It catches the divergence that actually happens in production — M02 upgrades a customer to
/// <c>Full</c> and the push to the CBS has not succeeded, so the two sides genuinely disagree. It
/// CANNOT see a tier changed by an officer inside the CBS itself: nothing in this platform ever
/// learns about that. A customer we have never pushed a tier for returns <c>null</c>, which the
/// snapshot stores as "the CBS does not expose one" and
/// <see cref="SnapshotKycLevels.Diverge"/> reads as "not a divergence" rather than as a mismatch
/// against every such customer.
/// </para>
///
/// <para>
/// <see cref="CommandType.SetKycLevel"/> is preferred over
/// <see cref="CommandType.CreateCustomer"/> only by recency, not by type: the creation payload
/// carries a tier too (<see cref="CbsCustomerPayload.KycLevel"/>), and for a customer created
/// before the onboarding chain pushed a tier it is the only statement there is.
/// </para>
/// </summary>
internal sealed class CbsKycLevelReader(IntegrationDbContext db, CommandPayloadProtector protector)
{
    internal async Task<KycLevel?> ReadAsync(
        Guid tenantId, Guid connectionId, Guid crmCustomerId, CancellationToken ct)
    {
        // IgnoreQueryFilters plus an explicit tenant predicate, the module's rule: the caller is
        // the synchronisation, a Hangfire job with no ambient tenant. Dropping the predicate is a
        // cross-tenant read.
        //
        // EntityType is filtered as well as CommandType. An account-opening command carries
        // EntityType "Account" with the CUSTOMER's id in CrmId (no module of this platform owns an
        // account record), so CrmId alone does not identify a customer command.
        var candidate = await db.Commands
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                        && c.ConnectionId == connectionId
                        && c.EntityType == IntegrationEntityTypes.Customer
                        && c.CrmId == crmCustomerId
                        && c.Status == CommandStatus.Succeeded
                        && (c.CommandType == CommandType.SetKycLevel
                            || c.CommandType == CommandType.CreateCustomer))
            .OrderByDescending(c => c.CompletedAt)
            .ThenByDescending(c => c.CreatedAt)
            .Select(c => new { c.CommandType, c.PayloadEncrypted })
            .FirstOrDefaultAsync(ct);

        if (candidate is null) return null;

        // Null rather than a guess when the payload no longer decrypts or no longer matches its
        // record — a rotated key, an older shape. Unprotect already answers null for both, and the
        // snapshot's null column is the honest outcome: reporting a mismatch computed from bytes
        // we could not read would send a compliance officer after a customer who is in order.
        return candidate.CommandType switch
        {
            CommandType.SetKycLevel =>
                protector.Unprotect<SetKycLevelPayload>(candidate.PayloadEncrypted)?.KycLevel,

            CommandType.CreateCustomer =>
                protector.Unprotect<CbsCustomerPayload>(candidate.PayloadEncrypted)?.KycLevel,

            _ => null,
        };
    }
}
