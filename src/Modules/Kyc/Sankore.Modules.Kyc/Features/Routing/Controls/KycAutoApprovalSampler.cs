namespace Sankore.Modules.Kyc.Features.Routing.Controls;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;

/// <summary>
/// Draws a share of the automatic approvals into the compliance queue (KRM-B-11).
///
/// <para>
/// <b>Drawn AT the approval, inside its transaction, and nowhere else.</b> The sample is of
/// APPROVALS, so it has to be taken when one happens: a nightly job picking rows would sample the
/// approvals that still exist and still look automatic, which is a different population — and one
/// that quietly shrinks every time a file is re-opened by a review.
/// </para>
///
/// <para>
/// <b>Why the draw is RANDOM and not a hash of the file id.</b> A deterministic draw would be
/// reproducible, which is attractive for an audit — and it would also be PREDICTABLE, which is
/// disqualifying: anybody who can influence which id a file gets could keep their approvals out of
/// the sample. A compliance control that can be steered is not one. Reproducibility is provided the
/// other way round, by the rows themselves: every draw leaves a durable record of what was picked,
/// and an institution answers a regulator from the table rather than by re-running a function.
/// </para>
///
/// <para>
/// It never throws and never fails the approval. A file approved by the policy is approved whether
/// or not it was drawn, and losing an approval over a sampling row would be a compliance control
/// causing the harm it exists to detect.
/// </para>
/// </summary>
internal sealed class KycAutoApprovalSampler(
    KycDbContext db,
    TimeProvider clock,
    ILogger<KycAutoApprovalSampler> logger)
{
    /// <summary>
    /// Adds a control row to the change tracker when this approval is drawn. The CALLER saves —
    /// the row must commit with the approval it is about, or a crash between the two leaves either
    /// an approval nobody will ever control or a control of an approval that never happened.
    /// </summary>
    /// <param name="samplePct">
    /// The tenant's <c>KYC_AUTO_APPROVAL_SAMPLE_PCT</c>. 0 draws nothing — an institution's right,
    /// though an unwise one; 100 draws everything, which is how a tenant runs the automatic mode
    /// under full review while it builds confidence in it. A value outside 0-100 is clamped rather
    /// than refused: this is not the place a misconfiguration should surface, and clamping 150 to
    /// 100 errs towards controlling more.
    /// </param>
    internal void Draw(
        KycFile file, KycTier grantedTier, int policyVersion, int samplePct)
    {
        try
        {
            var pct = Math.Clamp(samplePct, 0, 100);

            if (pct == 0) return;

            // RandomNumberGenerator and not Random.Shared: both are uniform enough for a 10%
            // draw, and this one cannot be seeded or replayed by anything in the process. For a
            // control whose whole value is that nobody can predict it, that is worth the call.
            if (pct < 100 && RandomNumberGenerator.GetInt32(100) >= pct) return;

            db.KycAutoApprovalControls.Add(KycAutoApprovalControl.Sample(
                tenantId: file.TenantId,
                kycFileId: file.Id,
                customerId: file.CustomerId,
                grantedTier: grantedTier,
                policyVersion: policyVersion,
                clock: clock));

            logger.LogInformation(
                "KYC file {KycFileId} drawn into the automatic-approval control queue "
                + "({Pct}% sample, policy v{Version})",
                file.Id, pct, policyVersion);
        }
        catch (Exception ex)
        {
            // See the class remarks: a sampling row must never cost an approval.
            logger.LogError(ex,
                "KYC file {KycFileId} could not be drawn into the control queue; the approval stands",
                file.Id);
        }
    }
}
