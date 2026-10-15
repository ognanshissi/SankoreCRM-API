namespace Sankore.Modules.Kyc.Features.RemoteInvitations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Hangfire job — mints the token of a scheduled collection link and mails it to the customer
/// (KRM-B-03).
///
/// <para>
/// <b>Why the token is minted HERE and not when the invitation was created.</b> The email is held
/// back a few minutes on purpose, so it arrives after the welcome message that carries the client
/// number the customer will be asked for. Minting at creation would mean carrying the clear token
/// through this job's arguments, and Hangfire arguments are rows in the shared application database
/// and lines on a dashboard behind basic auth — the two places the hash-only storage exists to keep
/// a bearer credential out of. So the clear token exists only inside this method: minted, hashed
/// into the row, formatted into a URL, handed to M08, gone.
/// </para>
///
/// <para>
/// <b>The payload is two opaque identifiers.</b> No address, no customer name, no product — all of
/// it is read here, under the tenant whose id is the second argument.
/// </para>
/// </summary>
public sealed class SendKycRemoteInvitationJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>The seeded template of the message the customer receives.</summary>
    public const string TemplateKey = "kyc.remote-invitation";

    /// <summary>
    /// The same link, worded for a customer who has already been through the journey once
    /// (KRM-B-10). A single template would have to say both "welcome, here is how to send your
    /// documents" and "your utility bill could not be read, please retake it", and the second
    /// message is the one that has to be unambiguous.
    /// </summary>
    public const string ComplementTemplateKey = "kyc.remote-complement";

    /// <summary>
    /// The SMS body carrying the link. ONE key for both purposes, unlike the e-mail side: a text
    /// message has no room to explain what a complement is, so the initial link and the second
    /// pass say the same short sentence and the page does the explaining.
    ///
    /// <para>
    /// Named here and seeded by M08 under the same key — a mismatch produces a message with no
    /// body, which the SMS processor dead-letters rather than sending.
    /// </para>
    /// </summary>
    public const string SmsTemplateKey = "kyc.remote-invitation";

    public async Task ExecuteAsync(Guid invitationId, Guid tenantId)
    {
        // Set BEFORE the DI scope is created: ITenantContext and ICurrentUser are built from this
        // ambient context when the scope resolves them, and a job has no HTTP request to fall back
        // on. SYSTEM is the right actor here and not the agent who converted the lead — sending a
        // link asserts nothing about anybody's identity.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<IKycRemoteLinkTokens>();
        var customers = scope.ServiceProvider.GetRequiredService<ICustomersModule>();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<SendKycRemoteInvitationJob>>();

        try
        {
            var invitation = await db.KycRemoteInvitations
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(i => i.TenantId == tenantId && i.Id == invitationId);

            if (invitation is null)
            {
                logger.LogWarning(
                    "Collection link {InvitationId} no longer exists; nothing sent.", invitationId);
                return;
            }

            // Revoked, or replaced by a newer link, in the minutes between the schedule and now.
            // Ordinary rather than exceptional: an agent who re-issues immediately produces
            // exactly this.
            if (invitation.Status != KycRemoteInvitationStatus.Scheduled)
            {
                logger.LogInformation(
                    "Collection link {InvitationId} is {Status} and no longer waiting to be sent.",
                    invitationId, invitation.Status);
                return;
            }

            var tenant = await tenants.GetAsync(tenantId);

            if (tenant is null || string.IsNullOrWhiteSpace(tenant.Fqdn))
            {
                // A configuration fault, not a business outcome, and the one failure here worth
                // shouting about: there is no host to build a link on, so no customer of this
                // tenant can ever be enrolled remotely. Left Scheduled so the nightly sweep
                // eventually expires it and the agent sees a link that never went out.
                logger.LogError(
                    "Tenant {TenantId} has no FQDN; collection link {InvitationId} cannot be built.",
                    tenantId, invitationId);
                return;
            }

            var (token, hash) = tokens.Mint();

            var attached = invitation.AttachTokenAndMarkSent(hash, clock);
            if (attached.IsFailure)
            {
                logger.LogWarning(
                    "Collection link {InvitationId} refused the token: {Error}",
                    invitationId, attached.Error);
                return;
            }

            // Committed BEFORE the mail is queued. The two cannot be made atomic — M08's queue is
            // another module's table and QueueEmailAsync is not part of this transaction — so the
            // order is chosen for which failure is survivable. Committing first can lose a mail
            // (the agent sees a Sent link the customer never got, and re-issues); mailing first
            // could send a link whose hash was never stored, which is a link that is guaranteed
            // not to work and looks to the customer like the institution's fault.
            await db.SaveChangesAsync();

            var link = tokens.BuildLink(tenant.Fqdn, token);

            var agencyName = await ResolveAgencyNameAsync(scope, tenantId, invitation.KycFileId);

            // KRM-B-10: a complement link is a different message, chosen from the invitation's
            // own purpose rather than from a job argument. The purpose is already persisted, so a
            // replayed job cannot send the wrong one — and the job's payload stays two ids.
            var isComplement = invitation.Purpose == KycRemoteInvitationPurpose.Complement;
            var templateKey = isComplement ? ComplementTemplateKey : TemplateKey;

            // ── Le canal ────────────────────────────────────────────────────
            //
            // Lu sur la LIGNE et non passé en argument : la charge utile du job reste deux
            // identifiants opaques, et un job rejoué ne peut pas envoyer par un canal que
            // personne n'a choisi.
            var queued = invitation.Channel == KycRemoteInvitationChannel.Sms
                ? await SendBySmsAsync(customers, tenantId, invitation, link)
                : await customers.NotifyClientAsync(new NotifyClientRequest(
                    TenantId: tenantId,
                    ClientId: invitation.CustomerId,
                    TemplateKey: templateKey,
                    // Keyed on the INVITATION and not on the file or the customer: a second link
                    // is a second legitimate message, and keying it on either of those would
                    // silence every re-issue. Matches the key KRM-B-03 names.
                    IdempotencyKey: $"{templateKey}-{invitationId}",
                    TemplateData: new Dictionary<string, object>
                    {
                        // The link, and deliberately NOT the client number. The customer already
                        // has it from the welcome message, and a mail carrying both the credential
                        // and the identifier it is checked against would make a single intercepted
                        // message enough to walk the whole flow.
                        ["link"] = link,
                        ["expires_at"] = invitation.ExpiresAt,
                        ["agency_name"] = agencyName ?? string.Empty,
                        // What to retake, in the customer's own words rather than as enum names.
                        // Empty on an initial link, and empty on a complement the agent did not
                        // scope — the template renders the list only when there is one, so a blank
                        // list reads as "the whole journey again", which is what an unscoped
                        // complement means.
                        ["items"] = isComplement
                            ? KycRemoteDocumentLabels.For(invitation.RequestedKinds())
                            : Array.Empty<string>(),
                    }), CancellationToken.None);

            if (!queued)
            {
                // The link is already marked Sent and its hash is stored, so it WORKS — it just
                // may not have reached anybody. Not rolled back: unwinding the status would let a
                // retry mint a second token and invalidate a link that may well have been
                // delivered. The agent's panel shows "envoyé" with no "ouvert", which is the
                // signal to re-issue.
                logger.LogWarning(
                    "Collection link {InvitationId} was minted but its email could not be queued.",
                    invitationId);
                return;
            }

            logger.LogInformation(
                "Collection link {InvitationId} mailed for KYC file {KycFileId}.",
                invitationId, invitation.KycFileId);
        }
        catch (Exception ex)
        {
            // Swallowed and logged rather than rethrown. Hangfire would retry on its own schedule,
            // and a retry that reaches AttachTokenAndMarkSent again is refused by the aggregate —
            // so the retry can only ever repeat the work that already failed. The nightly sweep is
            // what makes a link stuck in Scheduled visible.
            logger.LogError(ex,
                "Sending collection link {InvitationId} threw (tenant {TenantId}).",
                invitationId, tenantId);
        }
    }

    /// <summary>
    /// The agency the customer belongs to, for the message's "contact your branch" line.
    ///
    /// <para>
    /// Best-effort: a file opened while M01 could not answer carries no agency
    /// (<c>KycFile.AgencyId</c>), and the template renders the line empty rather than the mail
    /// failing. A missing branch name is a slightly poorer message; a missing link is a customer
    /// who cannot enrol.
    /// </para>
    /// </summary>
    /// <summary>
    /// The link, by text message.
    ///
    /// <para>
    /// <b>It carries the link and the deadline, and names nobody.</b> Same rule as the one-time
    /// code: a text message is readable on a locked screen, so naming the institution and the
    /// customer beside a bearer credential is what turns a stolen handset into an enrolment. No
    /// agency, and no list of documents to retake — a complement sent this way says "finish your
    /// file", and the page itself shows what is missing.
    /// </para>
    ///
    /// <para>
    /// The idempotency key is the same shape as the e-mail's, on the INVITATION: a second link is
    /// a second legitimate message, and keying it on the customer would silence every re-issue.
    /// The template key differs, because M08 keeps the two sets apart on purpose — sharing one
    /// would make an e-mail body the accidental fallback of a text message.
    /// </para>
    /// </summary>
    private static Task<bool> SendBySmsAsync(
        ICustomersModule customers,
        Guid tenantId,
        KycRemoteInvitation invitation,
        string link)
        => customers.NotifyClientBySmsAsync(new NotifyClientBySmsRequest(
            TenantId: tenantId,
            ClientId: invitation.CustomerId,
            TemplateKey: SmsTemplateKey,
            IdempotencyKey: $"{SmsTemplateKey}-{invitation.Id}",
            TemplateData: new Dictionary<string, object>
            {
                ["link"] = link,
                // Hours and not a formatted date: a date rendered into a text message costs
                // characters that the link has already spent, and "48h" is what a customer acts on.
                ["hours"] = Math.Max(
                    1, (int)Math.Round((invitation.ExpiresAt - invitation.CreatedAt).TotalHours)),
            }), CancellationToken.None);

    private static async Task<string?> ResolveAgencyNameAsync(
        IServiceScope scope, Guid tenantId, Guid kycFileId)
    {
        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();

        var agencyId = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.Id == kycFileId)
            .Select(f => f.AgencyId)
            .FirstOrDefaultAsync();

        if (agencyId is not { } id) return null;

        var administration = scope.ServiceProvider
            .GetRequiredService<Modules.Administration.PublicApi.IAdministrationModule>();

        var agency = await administration.GetAgencyAsync(tenantId, id, CancellationToken.None);
        return agency?.Name;
    }
}
