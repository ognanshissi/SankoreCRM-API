namespace Sankore.Modules.Integration.Features.Insurance.Claims.DeclareClaim;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens the dossier and queues the declaration. One transaction, for the reason the command's own
/// remarks give.
/// </summary>
internal sealed class DeclareClaimHandler(
    IntegrationDbContext db,
    IIntegrationModule integration,
    IFieldEncryptor encryptor,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<DeclareClaimHandler> logger)
    : IRequestHandler<DeclareClaimCommand, Result<DeclareClaimResult>>
{
    public async Task<Result<DeclareClaimResult>> Handle(
        DeclareClaimCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // The tenant query filter does the isolation: another tenant's policy is simply absent,
        // and the endpoint answers NOT_FOUND rather than FORBIDDEN — a policy's existence must
        // not leak.
        var policy = await db.Policies
            .Where(p => p.Id == cmd.PolicyId)
            .Select(p => new
            {
                p.Id,
                p.ConnectionId,
                p.CrmCustomerId,
                p.ExternalPolicyId,
                p.PolicyNumber,
                p.EffectiveDate,
                p.ExpiryDate,
            })
            .FirstOrDefaultAsync(ct);

        if (policy is null)
            return Result.Fail<DeclareClaimResult>(ClaimErrors.PolicyNotFound);

        // ── Was there cover on the day of the loss ──────────────────────────
        //
        // The DATES decide, never the policy's current status. A contract cancelled last month
        // covered the fire that happened while it was in force, and refusing that claim because
        // the policy reads Cancelled today would deny a customer a cover they had paid for.
        // Conversely a live policy does not cover a loss that predates its effective date.
        if (cmd.OccurredOn > today)
            return Result.Fail<DeclareClaimResult>(ClaimErrors.LossInTheFuture);

        if (cmd.OccurredOn < policy.EffectiveDate)
            return Result.Fail<DeclareClaimResult>($"{ClaimErrors.LossOutsideCover}:BEFORE_COVER");

        if (policy.ExpiryDate is { } expiry && cmd.OccurredOn > expiry)
            return Result.Fail<DeclareClaimResult>($"{ClaimErrors.LossOutsideCover}:AFTER_COVER");

        // ── Idempotence ─────────────────────────────────────────────────────
        //
        // Read first so a double-click gets the existing dossier instead of a 500 from
        // ux_ins_claim_declaration. The index is still what makes the rule true under
        // concurrency — EF InMemory honours no unique index, so this read is the only half the
        // test suite can prove.
        var existing = await db.Claims
            .Where(c => c.PolicyId == cmd.PolicyId
                     && c.OccurredOn == cmd.OccurredOn
                     && c.Nature == cmd.Nature.Trim())
            .Select(c => new { c.Id, c.Status, c.DeclareCommandId })
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            logger.LogInformation(
                "Claim {ClaimId} already declared for policy {PolicyId}, loss of {OccurredOn} "
                + "({Nature})",
                existing.Id, cmd.PolicyId, cmd.OccurredOn, cmd.Nature);

            return Result.Ok(new DeclareClaimResult(
                existing.Id, existing.Status.ToString(), AlreadyExisted: true,
                DeclareCommandId: existing.DeclareCommandId));
        }

        var claim = ClaimRecord.Create(
            tenantId: tenantId,
            connectionId: policy.ConnectionId,
            policyId: policy.Id,
            crmCustomerId: policy.CrmCustomerId,
            occurredOn: cmd.OccurredOn,
            nature: cmd.Nature,
            declaredBy: currentUser.Id,
            clock: clock,
            // Null stays null: an absent account must not become a decryptable blank.
            descriptionEncrypted: string.IsNullOrWhiteSpace(cmd.Description)
                ? null
                : encryptor.Encrypt(cmd.Description));

        db.Claims.Add(claim);

        // ── The declaration ────────────────────────────────────────────────
        //
        // The facade enlists in this unit of work and deliberately does not save, so the command
        // row and the dossier commit together.
        IntegrationCommandId commandId;

        try
        {
            commandId = await integration.Insurance.RequestClaimDeclarationAsync(
                new InsuranceClaimPayload(
                    PolicyId: new ExternalId(policy.ExternalPolicyId),
                    OccurredOn: cmd.OccurredOn,
                    Nature: cmd.Nature.Trim(),
                    // In CLEAR on the wire and encrypted in our column: the insurer has to be able
                    // to read the account of the loss, and the command payload is itself encrypted
                    // at rest by CommandPayloadProtector.
                    Description: cmd.Description?.Trim() ?? string.Empty,
                    // Deliberately empty — see the command's remarks. Nothing has been scanned at
                    // the moment a declaration is made, and a pièce de sinistre does not leave
                    // this platform before the antivirus pass has run on it.
                    DocumentStorageRefs: []),
                ct);
        }
        catch (DomainException ex)
        {
            // No adapter, no capability, or the insurer's connection is off. Nothing is saved, so
            // no dossier is left behind that nothing will ever send.
            logger.LogWarning(
                "Claim on policy {PolicyId} not declared: {Reason}", cmd.PolicyId, ex.Message);

            return Result.Fail<DeclareClaimResult>(ex.Message);
        }

        claim.AttachDeclareCommand(commandId.Value, clock);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Claim {ClaimId} declared on policy {PolicyNumber} at insurer {ConnectionId}; "
            + "queued as command {CommandId}",
            claim.Id, policy.PolicyNumber, policy.ConnectionId, commandId.Value);

        return Result.Ok(new DeclareClaimResult(
            claim.Id, claim.Status.ToString(), AlreadyExisted: false,
            DeclareCommandId: commandId.Value));
    }
}
