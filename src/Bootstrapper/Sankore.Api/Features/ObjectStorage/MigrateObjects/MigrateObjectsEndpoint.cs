namespace Sankore.Api.Features.ObjectStorage.MigrateObjects;

using MediatR;
using Sankore.Shared.Kernel;

internal static class MigrateObjectsEndpoint
{
    internal static RouteGroupBuilder MapMigrateObjects(this RouteGroupBuilder group)
    {
        group.MapPost("/{concern}/migration", async (
                string concern,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new MigrateObjectsCommand(concern), ct);

                // 202, not 200: the copy has been accepted and is running in Hangfire. The body
                // carries the job id so an operator can follow it, and the resolved source path so
                // they can confirm the server meant the folder they did.
                return result.IsSuccess
                    ? Results.Accepted(value: result.Value)
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("MigrateObjectStorage")
            .WithTags("Object storage")
            .WithOpenApi()
            .Produces<MigrateObjectsResult>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireAuthorization(Permissions.CanMigrateObjectStorage.Code)

            // The auth bucket, not the api one: this is an unauthenticated-adjacent platform lever
            // and a burst of calls would start a burst of concurrent full-volume copies.
            .RequireRateLimiting("auth");

        return group;
    }
}
