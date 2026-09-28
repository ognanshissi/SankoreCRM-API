namespace Sankore.Modules.Customers.Features.Compliance.Retention.AnonymizeClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Compliance.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class AnonymizeClientEndpoint
{
    public static IEndpointRouteBuilder MapAnonymizeClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("{clientId:guid}/anonymize", Handle)
            .WithName("AnonymizeClient")
            .WithSummary("Irreversibly erase an archived client's personal data")
            .WithDescription(
                "Drops names, encrypted payloads, blind indexes, phonetic keys and closes every " +
                "contact point; the client number and the aggregate stay so accounting history " +
                "keeps a stable reference. Refused with RETENTION_NOT_REACHED when the client is " +
                "not archived or the retention term has not elapsed, and with " +
                "KYC_RETENTION_NOT_CLEARED when the KYC module has not released the file. " +
                "Idempotent: anonymizing twice is a no-op. Audited — the audit entry is the only " +
                "trace left, so the reason must not itself contain personal data. " +
                "Requires permission: customers:archive.")
            .RequireAuthorization(Permissions.CanArchiveCustomer.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        AnonymizeClientRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new AnonymizeClientCommand(clientId, req.Reason), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : ComplianceHttp.ToProblem(result.Error);
    }
}

public sealed record AnonymizeClientRequest(string Reason);
