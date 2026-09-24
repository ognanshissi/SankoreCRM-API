namespace Sankore.Modules.Leads.Features.Ingestion.Pull;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Ingestion.Pull.DryRun;
using Sankore.Modules.Leads.Features.Ingestion.Pull.ManualPull;
using Sankore.Shared.Kernel;

public static class PullEndpoints
{
    public static IEndpointRouteBuilder MapPullEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /lead-sources/{id}/dry-run (synchronous, rate-limited 10/min/source)
        app.MapPost("lead-sources/{id:guid}/dry-run", HandleDryRun)
            .WithName("DryRunLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces<DryRunResult>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi();

        // POST /lead-sources/{id}/pull (async, 202 + run ID)
        app.MapPost("lead-sources/{id:guid}/pull", HandleManualPull)
            .WithName("ManualPullLeadSource")
            .WithTags("LeadSources")
            .RequireAuthorization(Permissions.CanManageLeadSources.Code)
            .Produces<ManualPullResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> HandleDryRun(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DryRunCommand(id), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : result.Error is "SOURCE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> HandleManualPull(
        Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ManualPullCommand(id), ct);

        if (!result.IsSuccess)
        {
            return result.Error switch
            {
                "SOURCE_NOT_FOUND" => Results.NotFound(),
                "RUN_ALREADY_IN_PROGRESS" => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(detail: result.Error, statusCode: 422)
            };
        }

        return Results.Accepted(value: new ManualPullResult(result.Value));
    }
}

public sealed record ManualPullResult(Guid RunId);
