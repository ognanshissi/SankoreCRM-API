using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;
using System.Reflection;

namespace Sankore.Modules.Administration.Features.Agencies.GetParentAgencies;

public static class GetParentAgenciesEndpoint
{
    private static readonly PropertyInfo[] AgencyDtoProperties = typeof(AgencyDto).GetProperties();

    public static IEndpointRouteBuilder MapGetParentAgencies(this IEndpointRouteBuilder app)
    {
        app.MapGet("parents", Handle)
            .WithName("GetParentAgencies")
            .WithSummary("Get all root agencies")
            .WithDescription(
                "Returns all non-deleted agencies with ParentAgencyId == null (root agencies). " +
                "Use the optional 'fields' query parameter to return only specific fields, e.g. fields=name,code. " +
                "Requires permission: agency:read.")
            .RequireAuthorization(Permissions.CanReadAgency.Code)
            .Produces<List<AgencyDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, string? fields, CancellationToken ct)
    {
        var result = await sender.Send(new GetParentAgenciesQuery(), ct);

        if (string.IsNullOrWhiteSpace(fields))
            return Results.Ok(result.Value);

        var requested = fields
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant())
            .ToHashSet();

        var selectedProps = AgencyDtoProperties
            .Where(p => requested.Contains(p.Name.ToLowerInvariant()))
            .ToArray();

        var projected = result.Value
            .Select(dto => selectedProps.ToDictionary(
                p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..],
                p => p.GetValue(dto)))
            .ToList();

        return Results.Ok(projected);
    }
}
