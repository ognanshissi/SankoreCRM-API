namespace Sankore.Modules.Integration.Features.Insurance.Claims;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Insurance.Claims.DeclareClaim;
using Sankore.Modules.Integration.Features.Insurance.Claims.RecordIndemnity;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// ASS-09: declaring a loss, following the dossier, and recording the indemnity.
///
/// <para>
/// Its own group and not under <c>products</c>: a dossier is acted on by its own id, which is what
/// an agent at the counter and an operator reconciling a credit both hold.
/// </para>
///
/// <para>
/// <b>There is no PUT and no DELETE.</b> A dossier's state belongs to the insurer and arrives
/// through the nightly sync; the two writes this platform owns are the declaration and the
/// indemnity credit, and each earns its own audit row. Editing a declared loss would rewrite what
/// was sent to an insurer.
/// </para>
/// </summary>
internal static class ClaimEndpoints
{
    internal static IEndpointRouteBuilder MapClaims(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("integration/insurance/claims").WithTags("Integration");

        group.MapPost(string.Empty, Declare)
            .WithName("DeclareInsuranceClaim")
            .WithSummary("Declare a loss on a policy")
            .WithDescription(
                "Opens the dossier and queues the declaration to the insurer in ONE transaction: "
                + "a dossier with no queued command is a claim the insurer never hears about "
                + "while the customer has been told it is in. "
                + "Cover is judged on the DATE of the loss, never on the policy's current "
                + "status — a contract cancelled last month covered the fire that happened while "
                + "it was in force. A loss already declared on the same policy, the same day, "
                + "with the same nature answers 200 with `alreadyExisted: true` rather than "
                + "opening a second dossier. "
                + "Documents do not travel with the declaration: a pièce de sinistre may not "
                + "leave the platform before the antivirus pass has run on it. "
                + "Requires permission: Ins.Claim.Declare.")
            .RequireAuthorization(Permissions.CanDeclareInsuranceClaim.Code)
            .Produces<DeclareClaimResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet(string.Empty, List)
            .WithName("ListInsuranceClaims")
            .WithSummary("A customer's dossiers, or the tenant's open ones")
            .WithDescription(
                "`crmCustomerId` narrows to one customer; `openOnly` keeps the dossiers the "
                + "insurer may still act on. The agent's account of the loss is NOT returned — "
                + "`hasDescription` says it is on file. "
                + "Requires permission: Ins.Claim.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceClaim.Code)
            .Produces<IReadOnlyList<ClaimDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{claimId:guid}", Get)
            .WithName("GetInsuranceClaim")
            .WithSummary("One dossier")
            .WithDescription(
                "`missingDocuments` is the insurer's own list of pièces, in its own words, and is "
                + "cleared as soon as it stops asking. "
                + "Requires permission: Ins.Claim.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceClaim.Code)
            .Produces<ClaimDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{claimId:guid}/indemnity", RecordIndemnity)
            .WithName("RecordInsuranceClaimIndemnity")
            .WithSummary("Record the indemnity credited to the customer")
            .WithDescription(
                "The insurer pays the indemnity by transfer into an account this platform only "
                + "observes, so the credit's reference arrives through whoever reconciled it — an "
                + "operator action with an audit row, not an event. Submitting the SAME reference "
                + "twice changes nothing; a DIFFERENT one on a dossier already paid is a 409, "
                + "because two credit references for one indemnity means one belongs to another "
                + "dossier. A dossier the insurer refused is a 409 too. "
                + "Requires permission: Ins.Claim.Declare.")
            .RequireAuthorization(Permissions.CanDeclareInsuranceClaim.Code)
            .Produces<ClaimDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Declare(
        DeclareClaimRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeclareClaimCommand(
            PolicyId: body.PolicyId,
            OccurredOn: body.OccurredOn,
            Nature: body.Nature,
            Description: body.Description), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        var error = result.Error!;

        if (error == ClaimErrors.PolicyNotFound)
            return Results.NotFound(new { error });

        // The request is well-formed and the STATE is what refuses it: the loss falls outside the
        // cover, or the insurer takes no declarations through this channel.
        if (error.StartsWith(ClaimErrors.LossOutsideCover, StringComparison.Ordinal)
            || error.StartsWith(ClaimErrors.InsurerCannotTakeClaims, StringComparison.Ordinal))
            return Results.Conflict(new { error });

        return Results.BadRequest(new { error });
    }

    private static async Task<IResult> List(
        ISender sender, CancellationToken ct, Guid? crmCustomerId = null, bool openOnly = false)
    {
        var result = await sender.Send(new ListClaimsQuery(crmCustomerId, openOnly), ct);

        return Results.Ok(result.Value);
    }

    private static async Task<IResult> Get(Guid claimId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetClaimQuery(claimId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> RecordIndemnity(
        Guid claimId, RecordClaimIndemnityRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RecordClaimIndemnityCommand(
            ClaimId: claimId,
            Amount: body.Amount,
            Currency: body.Currency,
            CbsReference: body.CbsReference), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        var error = result.Error!;

        if (error == ClaimErrors.ClaimNotFound)
            return Results.NotFound(new { error });

        if (error.StartsWith(ClaimErrors.IndemnityConflict, StringComparison.Ordinal))
            return Results.Conflict(new { error });

        return Results.BadRequest(new { error });
    }
}
