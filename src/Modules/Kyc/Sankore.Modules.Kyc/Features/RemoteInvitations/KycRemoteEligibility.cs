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
        KycRemotePolicyDto? Policy,
        string? SmsReason = null)
    {
        /// <summary>
        /// Whether a link can be issued AT ALL — by either channel. What the panel's banner reads.
        /// </summary>
        internal bool CanIssue => File is not null && (Reason is null || SmsReason is null);

        /// <summary>Whether the e-mail channel is open. <see cref="Reason"/> says why not.</summary>
        internal bool CanIssueByEmail => File is not null && Reason is null;

        /// <summary>Whether the SMS channel is open. <see cref="SmsReason"/> says why not.</summary>
        internal bool CanIssueBySms => File is not null && SmsReason is null;

        /// <summary>
        /// The refusal that applies to one channel, for a caller that has chosen one.
        ///
        /// <para>
        /// <b>Each field is filled whatever the cause</b>, including the file-level ones — the
        /// file no longer collecting, or the product forbidding remote collection, close both
        /// channels at once and are written into both. Leaving the SMS field null on those was
        /// the first version, and it made <see cref="CanIssue"/> answer true for a file nothing
        /// could be sent for: a null read as "this channel is fine" instead of "not asked".
        /// </para>
        /// </summary>
        internal string? ReasonFor(KycRemoteInvitationChannel channel)
            => channel == KycRemoteInvitationChannel.Sms ? SmsReason : Reason;
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
            return new Verdict(null, KycErrors.RemoteInvitationNotFound, null,
                KycErrors.RemoteInvitationNotFound);

        if (!CollectingStatuses.Contains(file.Status))
            return new Verdict(file, KycErrors.RemoteInvitationFileNotCollecting, null,
                KycErrors.RemoteInvitationFileNotCollecting);

        // ── Pas de ligne produit : la politique par défaut, et non un refus ──
        //
        // Deux des trois façons d'ouvrir un dossier n'en portent aucune — l'endpoint du
        // back-office et ClientCreatedKycConsumer — parce qu'à cet instant personne n'a choisi de
        // produit. Refuser ici faisait répondre « le produit souscrit n'autorise pas » à propos
        // d'un produit qui n'existe pas, et fermait la collecte à distance à tout dossier ouvert
        // depuis l'écran KYC. Voir KycRemoteDefaultPolicy pour ce que cette politique concède,
        // et surtout pour ce qu'elle refuse : la validation automatique.
        if (file.ProductLineId is not { } productLineId)
        {
            var emailOnlyReason = await ResolveEmailReasonAsync(tenantId, file.CustomerId, ct);

            return new Verdict(
                file,
                emailOnlyReason,
                KycRemoteDefaultPolicy.Instance,
                await ResolveSmsReasonAsync(tenantId, file.CustomerId, ct));
        }

        var policy = await administration.GetKycRemotePolicyAsync(tenantId, productLineId, ct);

        // A dangling product id and a product that forbids remote KYC both end here. They are
        // genuinely different — the first is a bug — but neither is something the agent can act on
        // differently, and distinguishing them in the message would mean explaining a dangling
        // reference to a branch employee.
        if (policy is null || !policy.Enabled)
            return new Verdict(file, KycErrors.RemoteInvitationProductNotEligible, policy,
                KycErrors.RemoteInvitationProductNotEligible);

        // Last, because these are the only checks that can change in the next minute and the only
        // ones an agent can fix themselves — so they are the most useful thing to be told when
        // they are the only thing missing.
        //
        // EVALUATED PER CHANNEL, and both are evaluated even when the first passes: the panel
        // offers two buttons and has to be able to grey out one of them with its own cause. A
        // single answer would make "no email" close the SMS route too.
        var emailReason = await ResolveEmailReasonAsync(tenantId, file.CustomerId, ct);
        var smsReason = await ResolveSmsReasonAsync(tenantId, file.CustomerId, ct);

        return new Verdict(file, emailReason, policy, smsReason);
    }

    /// <summary>Why the e-mail channel is closed, or null when it is open.</summary>
    private async Task<string?> ResolveEmailReasonAsync(
        Guid tenantId, Guid customerId, CancellationToken ct)
        => await customers.HasEmailAsync(tenantId, customerId, ct)
            ? null
            : KycErrors.RemoteInvitationNoEmail;

    /// <summary>
    /// Why the SMS channel is closed, or null when it is open.
    ///
    /// <para>
    /// Two conditions, and they fail for different people: the CUSTOMER must have a number on
    /// file, and the INSTITUTION must have a usable SMS channel. Collapsing them into one code
    /// would send an agent to edit a customer record over a provider their administrator never
    /// configured.
    /// </para>
    ///
    /// <para>
    /// <c>CanSend</c> is READ from M12 and never re-derived here: four conditions compose it —
    /// provider, sender identity, calling code, stored credential — and getting one wrong in the
    /// permissive direction promises a customer a message nobody will send. Same source as
    /// <c>SmsKycOtpSender.IsAvailableAsync</c>.
    /// </para>
    ///
    /// <para>
    /// An OUTAGE closes the channel rather than opening it. A refusal an agent can retry costs a
    /// click; a link reported as sent that never left costs a customer their enrolment.
    /// </para>
    /// </summary>
    private async Task<string?> ResolveSmsReasonAsync(
        Guid tenantId, Guid customerId, CancellationToken ct)
    {
        if (!await customers.HasPhoneAsync(tenantId, customerId, ct))
            return KycErrors.RemoteInvitationNoPhone;

        try
        {
            var sms = await administration.GetSmsConfigAsync(tenantId, ct);

            return sms?.CanSend == true ? null : KycErrors.RemoteInvitationSmsUnavailable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return KycErrors.RemoteInvitationSmsUnavailable;
        }
    }
}
