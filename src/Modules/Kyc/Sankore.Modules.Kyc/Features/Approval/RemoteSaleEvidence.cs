namespace Sankore.Modules.Kyc.Features.Approval;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Settings;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Kernel;

/// <summary>
/// CIMA LBC/FT article 13.6: a relationship entered into REMOTELY requires, alongside the identity
/// document, « une quittance de moins de trois mois attestant du domicile ».
///
/// <para>
/// <b>Which files are remote is already modelled</b> — <see cref="KycChannels.IsRemoteSale"/> — so
/// there is nothing to infer. A file opened at a counter or through an import is not a remote sale
/// and the requirement does not apply to it.
/// </para>
///
/// <para>
/// That predicate used to be spelled out here as <c>Web or MobileAgent</c>. It moved the day
/// <see cref="KycChannel.RemoteSelfService"/> was added, because the most remote channel of all
/// would otherwise have fallen outside the list and this gate would have stopped applying to it —
/// silently, and in the customer's favour.
/// </para>
///
/// <para>
/// <b>Checked at VALIDATION, not at submission.</b> A file is submitted for verification the
/// moment an agent has what they have; refusing the submission would stop the OCR and the face
/// match from running at all, so the agent could not even learn whether the identity document was
/// readable. The document is required before the customer becomes a customer, which is the final
/// approval.
/// </para>
/// </summary>
internal sealed class RemoteSaleEvidence(KycDbContext db, IKycSettings settings, TimeProvider clock)
{
    /// <summary>
    /// Refuses when a remote file has no acceptable proof of address. Success for every
    /// non-remote channel, and success as soon as one ACCEPTED, recent proof is on file.
    /// </summary>
    public async Task<Result> EnsureSatisfiedAsync(KycFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!KycChannels.IsRemoteSale(file.Channel))
            return Result.Ok();

        var maxAgeDays = await settings.GetIntAsync(
            file.TenantId, KycSettingKeys.ProofOfAddressMaxAgeDays, ct);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var earliest = today.AddDays(-maxAgeDays);

        // Accepted, not merely uploaded: a refused image is evidence the agent was asked for a
        // better one, and counting it would let a rejected utility bill open an account.
        //
        // A null issue date does NOT satisfy the rule. Reading it as "fresh" would pass every row
        // recorded before the column existed, which is the one outcome a freshness check must not
        // have — the document on file may be years old.
        var hasProof = await db.KycDocuments
            .AnyAsync(
                d => d.KycFileId == file.Id
                  && d.Kind == KycDocumentKind.ProofOfAddress
                  && d.ReviewDecision == KycDocumentReviewDecision.Accepted
                  && d.DocumentIssuedOn != null
                  && d.DocumentIssuedOn >= earliest
                  && d.DocumentIssuedOn <= today,
                ct);

        return hasProof
            ? Result.Ok()
            : Result.Fail(KycErrors.ProofOfAddressRequired);
    }
}
