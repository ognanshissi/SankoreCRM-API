namespace Sankore.Modules.Kyc.Features.RemoteInvitations;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;

/// <summary>
/// Whether a KYC file can be collected remotely, and when it cannot, WHY (KRM-B-03, KRM-F-07).
///
/// <para>
/// One service and not two copies of the rules, because two callers need the same answer for
/// different purposes: the issue command refuses, and the agent's panel explains. KRM-B-03 asks
/// for the refusal to be "visible pour l'agent", and a reason an agent reads has to be the same
/// reason the system acted on — a screen that says "no email" while the command refused for
/// another cause sends somebody to fix the wrong thing.
/// </para>
///
/// <para>
/// <b>Evaluated on every read rather than stored.</b> The three causes all change on their own: an
/// agent adds the missing email, an administrator enables remote KYC on the product, the file moves
/// on. A stored reason would keep telling an agent "pas d'email" minutes after they typed one in,
/// and the only way out of that is a column somebody has to remember to clear. It costs one cached
/// policy read and one indexed existence check per screen load.
/// </para>
/// </summary>
internal sealed class KycRemoteEligibility(
    KycDbContext db,
    IAdministrationModule administration,
    ICustomersModule customers)
{
    /// <summary>
    /// The file's state as the remote-collection rules see it.
    /// </summary>
    /// <param name="File">
    /// Null when the file does not exist in this tenant. Every other member is then meaningless.
    /// </param>
    /// <param name="Reason">
    /// Null when a link may be issued; otherwise one of the <c>KYC_REMOTE_INVITATION_*</c> codes,
    /// which the front translates.
    /// </param>
    /// <param name="Policy">
    /// The product's remote-KYC policy, or null when the file names no product or the product has
    /// vanished. Carried even on a refusal so a caller can show what the product says.
    /// </param>
    internal sealed record Verdict(
        KycFile? File,
        string? Reason,
        KycRemotePolicyDto? Policy)
    {
        internal bool CanIssue => File is not null && Reason is null;
    }

    /// <summary>
    /// The statuses a link may be issued in: the file is still collecting evidence.
    ///
    /// <para>
    /// <c>ComplementRequired</c> is here as well as <c>Collecting</c>, and that is deliberate even
    /// though the complement flow itself is KRM-B-10: a file sent back for a better photo is
    /// exactly the case where a second link saves a branch visit, and an agent re-issuing one by
    /// hand must not be refused in the meantime.
    /// </para>
    /// </summary>
    private static readonly KycFileStatus[] CollectingStatuses =
    [
        KycFileStatus.Collecting,
        KycFileStatus.ComplementRequired,
    ];

    internal async Task<Verdict> EvaluateAsync(
        Guid tenantId, Guid kycFileId, CancellationToken ct)
    {
        // IgnoreQueryFilters with an explicit tenant predicate: one caller is a MassTransit
        // consumer with no ambient tenant of its own.
        // NOT tracked: this is a read, and both callers call SaveChanges for other reasons in the
        // same scope. A tracked file sitting in the change tracker is how an unrelated commit
        // starts writing a row nobody meant to touch.
        var file = await db.KycFiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.Id == kycFileId, ct);

        if (file is null)
            return new Verdict(null, KycErrors.RemoteInvitationNotFound, null);

        if (!CollectingStatuses.Contains(file.Status))
            return new Verdict(file, KycErrors.RemoteInvitationFileNotCollecting, null);

        // No product means no policy, and no policy means no link. The restrictive reading of a
        // missing reference, as everywhere else in this module — and the agent is told it is the
        // PRODUCT that is the problem, because the fix is to convert on a catalogue line that
        // allows remote collection, not to chase an email address.
        if (file.ProductLineId is not { } productLineId)
            return new Verdict(file, KycErrors.RemoteInvitationProductNotEligible, null);

        var policy = await administration.GetKycRemotePolicyAsync(tenantId, productLineId, ct);

        // A dangling product id and a product that forbids remote KYC both end here. They are
        // genuinely different — the first is a bug — but neither is something the agent can act on
        // differently, and distinguishing them in the message would mean explaining a dangling
        // reference to a branch employee.
        if (policy is null || !policy.Enabled)
            return new Verdict(file, KycErrors.RemoteInvitationProductNotEligible, policy);

        // Last, because it is the only check that can change in the next minute and the only one
        // an agent can fix themselves — so it is the most useful thing to be told when it is the
        // only thing missing.
        if (!await customers.HasEmailAsync(tenantId, file.CustomerId, ct))
            return new Verdict(file, KycErrors.RemoteInvitationNoEmail, policy);

        return new Verdict(file, null, policy);
    }
}
