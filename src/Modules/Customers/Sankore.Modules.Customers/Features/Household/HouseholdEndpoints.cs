namespace Sankore.Modules.Customers.Features.Household;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Household.AddMember;
using Sankore.Modules.Customers.Features.Household.GetHousehold;
using Sankore.Modules.Customers.Features.Household.RemoveMember;
using Sankore.Modules.Customers.Features.Household.SetPepStatus;
using Sankore.Modules.Customers.Features.Household.Shared;
using Sankore.Modules.Customers.Features.Household.UpdateMember;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// A client's household and the political exposure of the people in it (BNF-B-13). Mounted under
/// <c>api/v1/clients/{clientId}/household</c>.
/// </summary>
public static class HouseholdEndpoints
{
    public static IEndpointRouteBuilder MapHouseholdEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("clients/{clientId:guid}/household")
            .WithTags("Client Household");

        group.MapGet(string.Empty, GetHousehold)
            .WithName("GetClientHousehold")
            .WithSummary("A client's spouse, children and dependents")
            .WithDescription(
                "Returns the household as it stands. PEP detail (function, country, family link) "
                + "requires customers:pep_read; the exposure flag is always visible. "
                + "Pass includeRemoved=true to also return members whose link has been closed.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<ClientHouseholdDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("members", AddMember)
            .WithName("AddClientHouseholdMember")
            .WithSummary("Record a spouse, a child or a dependent")
            .RequireAuthorization(Permissions.CanManageCustomerHousehold.Code)
            .Produces<AddHouseholdMemberResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("members/{memberId:guid}", UpdateMember)
            .WithName("UpdateClientHouseholdMember")
            .WithSummary("Correct a member's identity or a spouse's union type")
            .RequireAuthorization(Permissions.CanManageCustomerHousehold.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        // POST and not DELETE, because the body carries the required reason — and a DELETE with a
        // body fails ASP.NET's inference outright ("Body was inferred but the method does not
        // allow inferred body parameters"), which is a start-up error and not a 400.
        group.MapPost("members/{memberId:guid}/removal", RemoveMember)
            .WithName("RemoveClientHouseholdMember")
            .WithSummary("Close a member's link to the household, with a reason")
            .WithDescription(
                "Logical removal: the row is kept and flagged. Beneficiary designations the member "
                + "held move to ToReview and are never deleted — the response says how many.")
            .RequireAuthorization(Permissions.CanManageCustomerHousehold.Code)
            .Produces<RemoveHouseholdMemberResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("pep", SetClientPep)
            .WithName("SetClientPepStatus")
            .WithSummary("Declare the client's own political exposure")
            .RequireAuthorization(Permissions.CanManageCustomerHousehold.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("members/{memberId:guid}/pep", SetMemberPep)
            .WithName("SetClientHouseholdMemberPepStatus")
            .WithSummary("Declare one member's political exposure")
            .RequireAuthorization(Permissions.CanManageCustomerHousehold.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> GetHousehold(
        Guid clientId, bool includeRemoved, ISender sender, HttpContext http, CancellationToken ct)
    {
        // The PEP gate, decided HERE from the caller's claims — never inside the handler, so an
        // authorization reviewer finds it where they look for it.
        var canReadPep = http.User.HasClaim("permission", Permissions.CanReadCustomerPep.Code);

        var result = await sender.Send(
            new GetHouseholdQuery(clientId, canReadPep, includeRemoved), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : HouseholdHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> AddMember(
        Guid clientId, AddHouseholdMemberRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new AddHouseholdMemberCommand(
            ClientId:             clientId,
            Type:                 req.Type,
            Identity:             req.Identity,
            SpouseUnionType:      req.SpouseUnionType,
            Pep:                  req.Pep,
            UsesPrincipalAddress: req.UsesPrincipalAddress,
            RelatedClientId:      req.RelatedClientId), ct);

        return result.IsSuccess
            ? Results.Created(
                $"/api/v1/clients/{clientId}/household",
                new AddHouseholdMemberResponse(result.Value))
            : HouseholdHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> UpdateMember(
        Guid clientId, Guid memberId, UpdateHouseholdMemberRequest req,
        ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateHouseholdMemberCommand(
            ClientId:             clientId,
            MemberId:             memberId,
            Identity:             req.Identity,
            SpouseUnionType:      req.SpouseUnionType,
            UsesPrincipalAddress: req.UsesPrincipalAddress), ct);

        return result.IsSuccess ? Results.NoContent() : HouseholdHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> RemoveMember(
        Guid clientId, Guid memberId, RemoveHouseholdMemberRequest req,
        ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new RemoveHouseholdMemberCommand(clientId, memberId, req.Reason), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : HouseholdHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> SetClientPep(
        Guid clientId, PepStatusDto req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SetClientPepStatusCommand(clientId, req), ct);
        return result.IsSuccess ? Results.NoContent() : HouseholdHttp.ToProblem(result.Error);
    }

    private static async Task<IResult> SetMemberPep(
        Guid clientId, Guid memberId, PepStatusDto req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new SetHouseholdMemberPepStatusCommand(clientId, memberId, req), ct);

        return result.IsSuccess ? Results.NoContent() : HouseholdHttp.ToProblem(result.Error);
    }
}

/// <summary>
/// Supply either <paramref name="RelatedClientId"/> (the member is a client of the tenant) or
/// <paramref name="Identity"/> (they are not) — never both.
/// </summary>
public sealed record AddHouseholdMemberRequest(
    /// <summary>"Spouse" | "Child" | "Dependent".</summary>
    string Type,
    PersonIdentityDto? Identity = null,
    /// <summary>"Married" | "CivilUnion". Required for a spouse, forbidden otherwise.</summary>
    string? SpouseUnionType = null,
    PepStatusDto? Pep = null,
    bool UsesPrincipalAddress = false,
    Guid? RelatedClientId = null);

public sealed record AddHouseholdMemberResponse(Guid MemberId);

public sealed record UpdateHouseholdMemberRequest(
    PersonIdentityDto? Identity = null,
    string? SpouseUnionType = null,
    bool UsesPrincipalAddress = false);

public sealed record RemoveHouseholdMemberRequest(string Reason);
