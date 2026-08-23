namespace Sankore.Api.Features.Audit.GetAuditEntries;

using MediatR;
using Microsoft.AspNetCore.Mvc;
using Sankore.Shared.Kernel;

internal static class GetAuditEntriesEndpoint
{
    internal static RouteGroupBuilder MapGetAuditEntries(this RouteGroupBuilder group)
    {
        group.MapGet("/entries", async (
            [FromQuery] Guid? userId,
            [FromQuery] string? action,
            [FromQuery] string? resourceType,
            [FromQuery] string? resourceId,
            [FromQuery] string? outcome,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            ISender sender = default!,
            CancellationToken ct = default) =>
        {
            var query = new GetAuditEntriesQuery(userId, action, resourceType, resourceId, outcome, from, to, page, pageSize);
            var result = await sender.Send(query, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Results.Problem(result.Error);
        })
        .WithName("GetAuditEntries")
        .WithTags("Audit")
        .WithOpenApi()
        .Produces<PagedResult<AuditEntryDto>>(StatusCodes.Status200OK)
        .RequireAuthorization(Permissions.CanReadAudit.Code);

        return group;
    }
}
