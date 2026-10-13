namespace Sankore.Modules.Kyc.Features.Files.Consumers;

using MassTransit;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files.CreateKycFile;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Second automatic trigger of KYC-B-01: M13 publishes <see cref="KycRequestedIntegrationEvent"/>
/// from <c>ConvertLeadHandler</c> when a lead becomes a customer.
///
/// <para>
/// Redundant with <see cref="ClientCreatedKycConsumer"/> by design — a converted lead produces
/// both events for the same customer — and the redundancy is the point: the conversion must open
/// a file even on the day M01's own event is delayed, lost or filtered. The duplicate costs
/// nothing because <c>CreateKycFileHandler</c> is idempotent; the loser simply reports
/// <c>AlreadyExisted: true</c>.
/// </para>
///
/// <para>
/// The event carries identity fields (name, phone, national id, date of birth). None of them is
/// used here: this slice only opens the file, and copying a sensitive value into this module
/// before the verification phase can encrypt it would create a second, unprotected home for it.
/// </para>
///
/// <para>
/// <c>HasPepExposure</c> is the exception, and it is a FLAG rather than a value: it decides the
/// file's vigilance level, which decides who has to sign it (BNF-B-24). <c>ProductLineId</c> is the
/// second (KRM-B-02), and it is why this consumer still matters even when it loses the race: the
/// catalogue line exists on no other event reaching this module.
/// </para>
///
/// <para>
/// <b>It also asks for the remote-collection link, and KRM-B-03 said to trigger that on
/// <c>KycInitiated</c> instead.</b> That would not have worked, and the reason is this consumer's
/// own first paragraph: <c>ClientCreatedKycConsumer</c> usually wins, so <c>KycInitiated</c> is
/// published by a trigger that does not know the product — the link decision would then read a file
/// whose <c>ProductLineId</c> is still null and conclude the product is not eligible, for most
/// conversions. Issuing from here is deterministic: the product is in hand, the file is guaranteed
/// to exist (the command above either created it or found it), and the issue command is idempotent
/// on a file that already holds a usable link, so a redelivery mails nothing twice.
/// </para>
/// </summary>
public sealed class KycRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<KycRequestedConsumer> logger)
    : IConsumer<KycRequestedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<KycRequestedIntegrationEvent> context)
    {
        var evt = context.Message;

        // SYSTEM placeholder tenant: nothing to open for a tenant that does not exist.
        if (evt.TenantId == Guid.Empty) return;

        // The ambient scope must be established BEFORE any scoped service is resolved:
        // ITenantContext and ICurrentUser are built from it and a consumer has no HTTP context to
        // fall back on.
        using var bg = BackgroundJobContext.SetScope(evt.TenantId, evt.RequestedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            // The channel is not derived here: this event exists only on the lead-conversion path,
            // so LeadConversion is a fact of the trigger, not an inference.
            // RequestedBy is the agent who converted the lead — the accountable actor, never a
            // SYSTEM placeholder. See CreateKycFileCommand.InitiatedBy.
            var result = await sender.Send(
                new CreateKycFileCommand(
                    TenantId: evt.TenantId,
                    CustomerId: evt.CustomerEntityId,
                    Channel: KycChannel.LeadConversion,
                    InitiatedBy: evt.RequestedBy,
                    // BNF-B-24: a politically exposed household opens the file at High, which is
                    // what adds the compliance officer to the approval ladder. The level is
                    // RAISED and never lowered — see CreateKycFileHandler, which also applies it
                    // to a file ClientCreatedKycConsumer opened first. That race is the normal
                    // case here, so passing the level only on creation would mean the flag
                    // usually had no effect at all.
                    VigilanceLevel: evt.HasPepExposure
                        ? KycVigilanceLevel.High
                        : KycVigilanceLevel.Standard,
                    // Carried as its own fact: High alone does not call for the general
                    // management's authorisation, a PEP does.
                    IsPoliticallyExposed: evt.HasPepExposure,
                    // KRM-B-02. THE reason this consumer still matters even though M01's event
                    // usually opens the file first: the product line exists only on this event,
                    // and the file needs it to know whether the customer may send their documents
                    // from their own phone and how the result is validated.
                    ProductLineId: evt.ProductLineId),
                context.CancellationToken);

            if (result.IsFailure)
            {
                // Swallowed on purpose: a lead that will never be acceptable would otherwise be
                // redelivered forever. The absent file shows up in the compliance backlog.
                logger.LogWarning(
                    "No KYC file opened for customer {CustomerId} converted from lead {LeadId} " +
                    "(tenant {TenantId}): {Error}",
                    evt.CustomerEntityId, evt.LeadId, evt.TenantId, result.Error);
                return;
            }

            if (result.Value.AlreadyExisted)
            {
                // ClientCreatedKycConsumer got there first — the expected outcome, not an incident.
                logger.LogDebug(
                    "Customer {CustomerId} already had KYC file {KycFileId}",
                    evt.CustomerEntityId, result.Value.KycFileId);

                // And the link is still owed. This branch is the COMMON one for a converted lead,
                // so returning here is how the feature would have shipped doing nothing most of
                // the time — the trap the PEP flag fell into on this same path.
                await RequestRemoteInvitationAsync(
                    sender, evt, result.Value.KycFileId, context.CancellationToken);
                return;
            }

            logger.LogInformation(
                "KYC file {KycFileId} opened for customer {CustomerId} converted from lead {LeadId}",
                result.Value.KycFileId, evt.CustomerEntityId, evt.LeadId);

            await RequestRemoteInvitationAsync(
                sender, evt, result.Value.KycFileId, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancellation is the host shutting down and must keep propagating so the message is
            // redelivered; every other failure is logged and dropped, same reasoning as above.
            logger.LogWarning(
                ex, "KYC file creation threw for customer {CustomerId} (tenant {TenantId})",
                evt.CustomerEntityId, evt.TenantId);
        }
    }

    /// <summary>
    /// Asks for the customer's collection link (KRM-B-03), and treats every refusal as information
    /// rather than as a failure.
    ///
    /// <para>
    /// The three refusals — no product policy, product not eligible, no email on file — are all
    /// ORDINARY outcomes of a conversion, not faults: most catalogue lines will never enable remote
    /// KYC, and a lead captured in the field often has a phone number and nothing else. They are
    /// logged at Information and nothing is retried, because the agent's own screen re-evaluates
    /// the same three causes on every read and tells them which one it is (KRM-F-07).
    /// </para>
    /// </summary>
    private async Task RequestRemoteInvitationAsync(
        ISender sender, KycRequestedIntegrationEvent evt, Guid kycFileId, CancellationToken ct)
    {
        var invitation = await sender.Send(
            new Features.RemoteInvitations.IssueInvitation.IssueKycRemoteInvitationCommand(
                TenantId: evt.TenantId,
                KycFileId: kycFileId,
                // SYSTEM, not the converting agent: issuing a link asserts nothing about anybody's
                // identity, and the row says "the conversion did this" rather than naming somebody
                // who never clicked a button.
                IssuedBy: Guid.Empty,
                // A redelivered conversion event must not mail a second link nor invalidate the
                // one the customer may be reading at that moment.
                ReplaceActive: false,
                // Null means "read the tenant's remote-email-delay-minutes" — the link has to land
                // after the welcome message that carries the client number it will ask for.
                DelayMinutes: null),
            ct);

        if (invitation.IsFailure)
        {
            logger.LogInformation(
                "No remote-collection link for KYC file {KycFileId} of customer {CustomerId}: {Reason}",
                kycFileId, evt.CustomerEntityId, invitation.Error);
            return;
        }

        if (invitation.Value.Reused) return;

        logger.LogInformation(
            "Remote-collection link {InvitationId} requested for KYC file {KycFileId}",
            invitation.Value.InvitationId, kycFileId);
    }
}
