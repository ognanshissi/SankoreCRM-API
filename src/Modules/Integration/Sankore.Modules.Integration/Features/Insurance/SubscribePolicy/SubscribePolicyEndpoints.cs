namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// ASS-04 and ASS-05: subscribing a customer, and reading what became of the attempt.
/// </summary>
internal static class SubscribePolicyEndpoints
{
    internal static IEndpointRouteBuilder MapSubscribePolicy(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/insurance/subscriptions")
            .WithTags("Integration")
            .RequireTenantModule(TenantModules.Insurance);

        group.MapPost(string.Empty, Subscribe)
            .WithName("SubscribeInsurancePolicy")
            .WithSummary("Prepare the insurance subscription of a signed holding")
            .WithDescription(
                "The holding is the contract: it must be signed (PendingSignature with a signature) "
                + "and its catalogue line must be the one the insurer's product realises. This call "
                + "checks the product, the client's eligibility (age, KYC level, account, loan) and "
                + "the won project's questionnaires, then records the subscription, how the premium "
                + "is paid (`paymentMode`: a core banking debit on `accountRef`, or a premium the "
                + "agent collected — `paymentMethod`, `paymentReference`, `paidOn`) and the consent "
                + "proof, in ONE transaction. Nothing is debited and nothing reaches the insurer "
                + "until the holding is sent (`POST holdings/{id}/submission`): the premium is "
                + "collected first, then the insurer asked, and the holding becomes Active when the "
                + "policy is issued. Preparing again before sending replaces the previous "
                + "preparation; after sending it answers `alreadyExisted: true`. "
                + "Requires permission: Ins.Policy.Subscribe.")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces<SubscribeInsurancePolicyResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{subscriptionId:guid}/payment", RecordPayment)
            .WithName("RecordInsurancePremiumPayment")
            .WithSummary("Record a premium collected after the contract was sent")
            .WithDescription(
                "For a subscription sent without its premium (the 60-day derogation, or an insurer "
                + "that collects) when the distributor holds the insurer's collection mandate. The "
                + "holding becomes Active once the policy is issued. 409 without the mandate or when "
                + "no premium is outstanding. Requires permission: Ins.Policy.Subscribe.")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet(string.Empty, ListForClient)
            .WithName("ListInsuranceSubscriptions")
            .WithSummary("A client's subscriptions, newest first")
            .WithDescription(
                "What the client record follows after sending: payment, the insurer's answer, the "
                + "client's answer, the 15 days. Within the agent's perimeter. Requires permission: Ins.Policy.View.")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces<IReadOnlyList<InsuranceSubscriptionDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{subscriptionId:guid}", Get)
            .WithName("GetInsuranceSubscription")
            .WithSummary("The state of one subscription attempt")
            .WithDescription(
                "ASS-05's « état global de la souscription », which no single command row can "
                + "answer. `owesRefund` is the state a human must act on: the premium was "
                + "collected, the insurer refused and the reversal has not gone through. "
                + "`reversalAttempts` is exposed because a refused reversal is NOT retried "
                + "automatically — an operator needs to know the core banking system has already "
                + "said no. The consent and medical evidence appear as BOOLEANS: a compliance "
                + "screen needs to know they are on file, not to read them. "
                + "Requires permission: Ins.Policy.View.")
            .RequireAuthorization(Permissions.CanViewInsurancePolicy.Code)
            .Produces<InsuranceSubscriptionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Subscribe(
        SubscribeInsurancePolicyRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SubscribeInsurancePolicyCommand(
            HoldingId: body.HoldingId,
            ProductId: body.ProductId,
            PaymentMode: body.PaymentMode,
            AccountRef: body.AccountRef,
            PaymentMethod: body.PaymentMethod,
            PaymentReference: body.PaymentReference,
            PaidOn: body.PaidOn,
            PremiumAmount: body.PremiumAmount,
            Currency: body.Currency,
            ConsentChannel: body.ConsentChannel,
            ConsentTextVersion: body.ConsentTextVersion,
            ConsentedAt: body.ConsentedAt,
            ConsentTextSha256: body.ConsentTextSha256,
            ConsentEvidence: body.ConsentEvidence,
            ConsentEvidenceStorageRef: body.ConsentEvidenceStorageRef,
            ConsentEvidenceSha256: body.ConsentEvidenceSha256,
            ConsentEvidenceContentType: body.ConsentEvidenceContentType,
            CommitmentReference: body.CommitmentReference,
            CommitmentSignedOn: body.CommitmentSignedOn,
            PremiumPayerClientId: body.PremiumPayerClientId), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        var error = result.Error!;

        if (error is InsuranceErrors.ProductNotFound or InsuranceErrors.HoldingNotFound)
            return Results.NotFound(new { error });

        // The request is well-formed and the STATE refuses it — the product, the client, the
        // institution or the holding is not in a condition to subscribe. A conflict, not a 400.
        if (error.StartsWith(InsuranceErrors.ProductNotOfferable, StringComparison.Ordinal)
            || error.StartsWith(InsuranceErrors.NotEligible, StringComparison.Ordinal)
            || error.StartsWith(InsuranceErrors.QuestionnairesIncomplete, StringComparison.Ordinal)
            || error.StartsWith(InsuranceErrors.DistributorNotInGoodStanding, StringComparison.Ordinal)
            || error is InsuranceErrors.PayerNotFound or InsuranceErrors.PayerKycNotApproved
            || error is InsuranceErrors.HoldingNotSigned or InsuranceErrors.SubscriptionInFlight
                     or InsuranceErrors.CoreBankingUnavailable or InsuranceErrors.NoCollectionMandate
                     or InsuranceErrors.InformationSheetNotDelivered or InsuranceErrors.DerogationNotAllowed)
            return Results.Conflict(new { error });

        return Results.BadRequest(new { error });
    }

    private static async Task<IResult> ListForClient(
        Guid crmCustomerId, IntegrationDbContext db, ICustomersModule customers, ICurrentUser user,
        TimeProvider clock, CancellationToken ct)
    {
        if (!await customers.CanActorAccessClientAsync(user.TenantId, crmCustomerId, user.Id, ct))
            return Results.NotFound(new { error = "CLIENT_NOT_FOUND" });

        var sagas = await db.InsuranceSubscriptions.Where(s => s.CrmCustomerId == crmCustomerId)
            .OrderByDescending(s => s.CreatedAt).Take(100).ToListAsync(ct);
        var ids = sagas.Select(s => s.Id).ToList();
        var consents = (await db.ConsentProofs.Where(p => ids.Contains(p.SubscriptionId)).Select(p => p.SubscriptionId).ToListAsync(ct)).ToHashSet();
        var medical = (await db.MedicalQuestionnaires.Where(q => ids.Contains(q.SubscriptionId)).Select(q => q.SubscriptionId).ToListAsync(ct)).ToHashSet();
        var now = clock.GetUtcNow();

        return Results.Ok(sagas.Select(s => InsuranceSubscriptionDto.Build(s, consents.Contains(s.Id), medical.Contains(s.Id), now)).ToList());
    }

    private static async Task<IResult> RecordPayment(
        Guid subscriptionId,
        RecordPremiumPaymentRequest body,
        IntegrationDbContext db,
        ICustomersModule customers,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken ct)
    {
        var saga = await db.InsuranceSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

        if (saga is null || !await customers.CanActorAccessClientAsync(user.TenantId, saga.CrmCustomerId, user.Id, ct))
            return Results.NotFound(new { error = InsuranceErrors.SubscriptionNotFound });

        var mandated = await db.Connections.AnyAsync(c => c.Id == saga.ConnectionId && c.HasCollectionMandate, ct);

        if (!mandated)
            return Results.Conflict(new { error = InsuranceErrors.NoCollectionMandate });

        // The premium itself, or an accepted surcharge collected afterwards (lot 3).
        if (!saga.IsPremiumOutstanding && !saga.IsSurchargeOutstanding)
            return Results.Conflict(new { error = "INSURANCE_PREMIUM_NOT_OUTSTANDING" });

        if (body.PaidOn > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            return Results.BadRequest(new { error = InsuranceErrors.PaymentDateInFuture });

        try
        {
            saga.RecordLatePayment(body.PaymentMethod, body.PaymentReference, body.PaidOn, clock);
        }
        catch (DomainException ex)
        {
            return Results.BadRequest(new { error = $"{InsuranceErrors.PaymentInvalid}: {ex.Message}" });
        }

        await db.SaveChangesAsync(ct);

        // An issued policy waiting for this premium becomes Active now; one not yet issued becomes
        // Active when the insurer answers, the premium no longer being outstanding.
        if (saga.CrmHoldingId is { } holdingId)
            await customers.ConfirmHoldingPaymentAsync(user.TenantId, holdingId, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> Get(
        Guid subscriptionId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetInsuranceSubscriptionQuery(subscriptionId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
