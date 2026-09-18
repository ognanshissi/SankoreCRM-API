namespace Sankore.Modules.Leads.Features.MergeLeads;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class MergeLeadsEndpoint
{
    public static IEndpointRouteBuilder MapMergeLeads(this IEndpointRouteBuilder app)
    {
        app.MapPost("{targetLeadId:guid}/merge", Handle)
            .WithName("MergeLeads")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanMergeLeads.Code)
            .Produces<MergeLeadResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid targetLeadId,
        MergeLeadsRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var mergedBy = http.User.GetUserId();

        var result = await sender.Send(
            new MergeLeadsCommand(
                TargetLeadId:     targetLeadId,
                SourceLeadId:     req.SourceLeadId,
                MergedBy:         mergedBy,
                FieldPreferences: req.FieldPreferences), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "TARGET_LEAD_NOT_FOUND" or "SOURCE_LEAD_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Lead merge failed", detail: result.Error, statusCode: 422);
        }

        return Results.Ok(result.Value);
    }
}

/// <param name="SourceLeadId">The duplicate lead to be archived and merged into the target.</param>
/// <param name="FieldPreferences">
/// Optional field-level selection: set Take* = true for each field whose value should be
/// taken from the SOURCE rather than kept from the TARGET. When omitted, all target
/// field values are preserved unchanged.
/// </param>
public sealed record MergeLeadsRequest(
    Guid SourceLeadId,
    MergeFieldPreferences? FieldPreferences = null);
