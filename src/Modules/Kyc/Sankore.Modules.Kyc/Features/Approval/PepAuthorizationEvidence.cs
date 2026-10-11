namespace Sankore.Modules.Kyc.Features.Approval;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// What the CIMA LBC/FT text requires before an institution enters into a relationship with a
/// POLITICALLY EXPOSED person, beyond the authorisation itself: « établir l'origine du patrimoine
/// et des fonds ».
///
/// <para>
/// The authorisation is the <c>GeneralManagement</c> rung of the ladder — a signature, which the
/// circuit arranges. This is the other half: management cannot authorise a relationship whose
/// funding nobody has established, so the final approval refuses while the client record carries
/// no recorded origin.
/// </para>
///
/// <para>
/// <b>Only the boolean crosses the boundary.</b> <c>ICustomersModule.HasRecordedFundsOriginAsync</c>
/// answers whether the work was done; the narrative behind it describes a named person's assets
/// and stays encrypted in M01, reachable only through that module's audited reveal path.
/// </para>
///
/// <para>
/// <b>Fails CLOSED on an outage.</b> A cross-module read that cannot be made is not evidence that
/// the origin was established — and unlike a capture, where losing a prospect is worse than not
/// recognising a customer, nothing is lost by making a validator retry. The same posture
/// <c>LeadAccessGuard</c> takes, and for the same kind of decision.
/// </para>
/// </summary>
internal sealed class PepAuthorizationEvidence(
    ICustomersModule customers,
    ILogger<PepAuthorizationEvidence> logger)
{
    public async Task<Result> EnsureSatisfiedAsync(KycFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!file.IsPoliticallyExposed)
            return Result.Ok();

        try
        {
            var recorded = await customers.HasRecordedFundsOriginAsync(
                file.TenantId, file.CustomerId, ct);

            return recorded ? Result.Ok() : Result.Fail(KycErrors.FundsOriginRequired);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Could not read whether the funds origin of customer {CustomerId} is recorded "
                + "(tenant {TenantId}); the PEP file is refused rather than validated",
                file.CustomerId, file.TenantId);

            return Result.Fail(KycErrors.FundsOriginRequired);
        }
    }
}
