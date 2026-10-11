namespace Sankore.Modules.Customers.Features.Holdings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Holdings.Designate;
using Sankore.Modules.Customers.Features.Holdings.GetHolding;
using Sankore.Modules.Customers.Features.Holdings.ListHoldings;
using Sankore.Modules.Customers.Features.Holdings.ListStalledHoldings;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Features.Holdings.Subscribe;
using Sankore.Modules.Customers.Features.Holdings.Transitions;
using Sankore.Modules.Customers.Features.Holdings.UpdateSpecifics;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The products a client holds and who is designated on them (BNF-B-14, BNF-B-15).
///
/// <para>
/// Two groups, because the two have different natural keys: a holding is created and listed under
/// its CLIENT, and then acted on by its OWN id — which is what a back-office reference and a
/// reconciliation run both carry.
/// </para>
/// </summary>
public static class HoldingsEndpoints
{
    public static IEndpointRouteBuilder MapHoldingsEndpoints(this IEndpointRouteBuilder app)
    {
        var perClient = app.MapGroup("clients/{clientId:guid}/holdings")
            .WithTags("Product Holdings");

        perClient.MapGet(string.Empty, List)
            .WithName("ListClientHoldings")
            .WithSummary("The products a client holds")
            .WithDescription(
                "Newest first. Filter by category (a ProductCategory name, matched against the "
                + "snapshot taken at subscription) and by status (a HoldingStatus name). "
                + "Each row carries canEdit, so a screen does not re-derive the state machine.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<PagedResult<HoldingDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        perClient.MapPost(string.Empty, Subscribe)
            .WithName("SubscribeClientToProduct")
            .WithSummary("Open a draft subscription")
            .WithDescription(
                "Refuses with HOLDING_ALREADY_HELD when the product line forbids multiple "
                + "holdings and the client already has a live one. Designations may be supplied "
                + "with the subscription; an invalid clause refuses the whole request.")
            .RequireAuthorization(Permissions.CanManageCustomerHolding.Code)
            .Produces<SubscribeResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        // Tenant-wide, so it cannot live under either group: an operations list of what is
        // stuck has no client and no holding in its route.
        app.MapGet("holdings/stalled", ListStalled)
            .WithName("ListStalledHoldings")
            .WithSummary("Signed holdings queued for a back office for too long")
            .WithDescription(
                "Holdings in PendingSync for at least `minAgeHours` (default 24), oldest first, "
                + "across the whole tenant. A subscription whose insurer cannot be determined is "
                + "refused inside the Integration consumer, which logs one warning and leaves "
                + "the holding here for ever — the reconciliation run cannot see it, because a "
                + "holding that was never queued has no integration reference. Pair it with "
                + "GET integration/insurance/products/product-routing, which says which product "
                + "lines will fail BEFORE a client signs. Carries no client identity.")
            .RequireAuthorization(Permissions.CanManageCustomerHolding.Code)
            .Produces<PagedResult<StalledHoldingDto>>(StatusCodes.Status200OK)
            .WithOpenApi()
            .WithTenantHeader();

        var byId = app.MapGroup("holdings/{holdingId:guid}")
            .WithTags("Product Holdings");

        byId.MapGet(string.Empty, Get)
            .WithName("GetHolding")
            .WithSummary("One holding with its current beneficiary clause")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<HoldingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        byId.MapPut("specifics", UpdateSpecifics)
            .WithName("UpdateHoldingSpecifics")
            .WithSummary("Replace a holding's terms, before the client signs")
            .RequireAuthorization(Permissions.CanManageCustomerHolding.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        byId.MapPut("designations", Designate)
            .WithName("SetHoldingDesignations")
            .WithSummary("Replace the beneficiary clause, as one atomic set")
            .WithDescription(
                "Roles and cardinalities come from the product line's policy; the shares of a rank "
                + "must total exactly 100% when the role says so. On a live holding this writes a "
                + "new version — an avenant — and closes the previous one.")
            .RequireAuthorization(Permissions.CanManageDesignations.Code)
            .Produces<DesignateResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        // Each transition is its own POST on a noun. Not a single PATCH on `status`: the
        // transitions take different inputs (a signature reference, a close reason) and carry
        // different permissions and different audit meanings, and one endpoint would have to
        // re-derive which from the target value.
        byId.MapPost("signature-request", RequestSignature)
            .WithName("RequestHoldingSignature")
            .WithSummary("Declare the file complete and awaiting signature")
            .WithDescription(
                "Refuses with DESIGNATION_REQUIRED_ROLE_MISSING when the product line requires a "
                + "role the clause does not satisfy — before the client signs, not after.")
            .RequireAuthorization(Permissions.CanManageCustomerHolding.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        byId.MapPost("signature", Sign)
            .WithName("SignHolding")
            .WithSummary("Record the client's signature and its evidence")
            .RequireAuthorization(Permissions.CanManageCustomerHolding.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        byId.MapPost("submission", Submit)
            .WithName("SubmitHolding")
            .WithSummary("Queue a signed holding for its back office")
            .RequireAuthorization(Permissions.CanManageCustomerHolding.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        byId.MapPost("return-to-draft", ReturnToDraft)
            .WithName("ReturnHoldingToDraft")
            .WithSummary("Send a rejected holding back for correction")
            .WithDescription("The recorded signature is cleared: the corrected terms are not what the client signed.")
            .RequireAuthorization(Permissions.CanManageCustomerHolding.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        byId.MapPost("closure", Close)
            .WithName("CloseHolding")
            .WithSummary("End a holding, with a reason")
            .RequireAuthorization(Permissions.CanManageCustomerHolding.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> List(
        Guid clientId, string? category, string? status, int? page, int? pageSize,
        ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new ListHoldingsQuery(clientId, category, status, page ?? 1, pageSize ?? 20), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> ListStalled(
        int? minAgeHours, int? page, int? pageSize, ISender sender, CancellationToken ct)
    {
        // 24 hours by default: a few minutes in PendingSync is the healthy state of a queued
        // command, so a lower default would report every subscription in flight as a fault.
        var result = await sender.Send(
            new ListStalledHoldingsQuery(minAgeHours ?? 24, page ?? 1, pageSize ?? 50), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> Get(Guid holdingId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetHoldingQuery(holdingId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> Subscribe(
        Guid clientId, SubscribeRequest req, ISender sender, CancellationToken ct)
    {
        // SourceLeadId is deliberately not taken from the body — see SubscribeToProductCommand.
        var result = await sender.Send(new SubscribeToProductCommand(
            ClientId:      clientId,
            ProductLineId: req.ProductLineId,
            Specifics:     req.Specifics,
            Designations:  req.Designations), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/holdings/{result.Value}", new SubscribeResponse(result.Value))
            : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> UpdateSpecifics(
        Guid holdingId, HoldingSpecificsDto req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateHoldingSpecificsCommand(holdingId, req), ct);
        return result.IsSuccess ? Results.NoContent() : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> Designate(
        Guid holdingId, DesignateRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new DesignateCommand(holdingId, req.Designations ?? []), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> RequestSignature(
        Guid holdingId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RequestSignatureCommand(holdingId), ct);
        return result.IsSuccess ? Results.NoContent() : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> Sign(
        Guid holdingId, SignHoldingRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new SignHoldingCommand(holdingId, req.SignatureReference, req.SignedAt), ct);

        return result.IsSuccess ? Results.NoContent() : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> Submit(Guid holdingId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SubmitHoldingCommand(holdingId), ct);
        return result.IsSuccess ? Results.NoContent() : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> ReturnToDraft(
        Guid holdingId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ReturnHoldingToDraftCommand(holdingId), ct);
        return result.IsSuccess ? Results.NoContent() : HoldingHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> Close(
        Guid holdingId, CloseHoldingRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CloseHoldingCommand(holdingId, req.Reason), ct);
        return result.IsSuccess ? Results.NoContent() : HoldingHttp.ToProblem(result.Error);
    }
}

public sealed record SubscribeRequest(
    Guid ProductLineId,
    HoldingSpecificsDto? Specifics = null,
    /// <summary>Optional: the clause may be recorded with the subscription or afterwards.</summary>
    IReadOnlyList<DesignationInput>? Designations = null);

public sealed record SubscribeResponse(Guid HoldingId);

/// <summary>
/// An empty list is a valid clause and clears it — for a line that requires no role. Where a role
/// is required, <c>signature-request</c> is what refuses.
/// </summary>
public sealed record DesignateRequest(IReadOnlyList<DesignationInput>? Designations);

public sealed record SignHoldingRequest(string SignatureReference, DateTimeOffset? SignedAt = null);

public sealed record CloseHoldingRequest(string Reason);
