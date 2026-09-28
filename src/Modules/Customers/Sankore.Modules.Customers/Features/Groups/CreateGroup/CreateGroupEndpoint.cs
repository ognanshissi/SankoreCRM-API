namespace Sankore.Modules.Customers.Features.Groups.CreateGroup;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class CreateGroupEndpoint
{
    public static IEndpointRouteBuilder MapCreateGroup(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("CreateClientGroup")
            .WithSummary("Create a client group (solidarity group, tontine or VSLA)")
            .WithDescription(
                "Creates the group in status Forming. It reaches Active automatically once it holds at least " +
                "group-min-size-<type> active members AND a President, a Treasurer and a Secretary. " +
                "A name already used inside the same agency returns 409 GROUP_NAME_ALREADY_USED. " +
                "An agency outside the caller's perimeter returns 403 AGENCY_OUT_OF_SCOPE. " +
                "Requires permission: customers:groups_manage.")
            .RequireAuthorization(Permissions.CanManageCustomerGroups.Code)
            .Produces<CreateGroupResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateGroupRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateGroupCommand(
            Type: req.Type,
            Name: req.Name,
            AgencyId: req.AgencyId,
            ConstitutionDate: req.ConstitutionDate), ct);

        if (result.IsSuccess)
            return Results.Created($"/api/v1/client-groups/{result.Value.GroupId}", result.Value);

        return result.Error switch
        {
            CustomerErrors.GroupNameAlreadyUsed => Results.Conflict(new { error = result.Error }),
            CustomerErrors.AgencyOutOfScope => Results.Problem(
                detail: result.Error, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(
                title: "Group creation failed", detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

public sealed record CreateGroupRequest(
    GroupType Type,
    string Name,
    Guid AgencyId,
    DateOnly ConstitutionDate);
