namespace Sankore.Modules.Integration.Features.Insurance.SubscribePolicy;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            .WithSummary("Subscribe a customer to an insurance product")
            .WithDescription(
                "Starts the three-step chain of ASS-05: the premium is debited FIRST, then the "
                + "insurer is asked, and the debit is reversed if the insurer refuses. That order "
                + "is deliberate — a premium collected against a refused policy can be put right, "
                + "a policy issued against a premium never collected cannot. "
                + "The consent proof required by the 2024 CIMA regulation on distribution by "
                + "electronic means is written in the SAME transaction as the subscription: "
                + "`consentTextVersion` plus either `consentEvidence` (encrypted inline) or "
                + "`consentEvidenceStorageRef`. A request for a customer, product and effective "
                + "date already in flight answers 200 with `alreadyExisted: true` rather than "
                + "debiting twice. Requires permission: Ins.Policy.Subscribe.")
            .RequireAuthorization(Permissions.CanSubscribeInsurancePolicy.Code)
            .Produces<SubscribeInsurancePolicyResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
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
            ProductId: body.ProductId,
            CrmCustomerId: body.CrmCustomerId,
            EffectiveDate: body.EffectiveDate,
            AccountRef: body.AccountRef,
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
            MedicalQuestionnaireCode: body.MedicalQuestionnaireCode,
            MedicalAnswers: body.MedicalAnswers,
            RequiresMedicalUnderwriting: body.RequiresMedicalUnderwriting,
            Beneficiaries: body.Beneficiaries), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        var error = result.Error!;

        if (error == InsuranceErrors.ProductNotFound)
            return Results.NotFound(new { error });

        // The product cannot be sold right now — a withdrawn catalogue entry, a deactivated
        // connection, a closed validity window. A conflict and not a 400: the request is
        // well-formed and the state is what refuses it.
        if (error.StartsWith(InsuranceErrors.ProductNotOfferable, StringComparison.Ordinal))
            return Results.Conflict(new { error });

        return Results.BadRequest(new { error });
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
