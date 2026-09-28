namespace Sankore.Modules.Customers.Features.Duplicates.BackfillPhoneticKeys;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class BackfillPhoneticKeysEndpoint
{
    public static IEndpointRouteBuilder MapBackfillPhoneticKeys(this IEndpointRouteBuilder app)
    {
        app.MapPost("backfill-phonetic-keys", Handle)
            .WithName("BackfillClientPhoneticKeys")
            .WithSummary("Recompute missing client phonetic keys")
            .WithDescription(
                "Queues a background job that recomputes the phonetic keys of every client of the " +
                "tenant whose primary key is still null. Duplicate detection blocks on those keys, " +
                "so clients without them are never compared. Returns 202 with the Hangfire job id. " +
                "Requires permission: customers:merge.")
            .RequireAuthorization(Permissions.CanMergeCustomers.Code)
            .Produces<BackfillPhoneticKeysResult>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new BackfillPhoneticKeysCommand(), ct);

        return result.IsSuccess
            ? Results.Accepted(value: result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
