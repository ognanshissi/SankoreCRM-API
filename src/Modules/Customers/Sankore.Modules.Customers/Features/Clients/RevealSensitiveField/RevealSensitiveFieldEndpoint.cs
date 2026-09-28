namespace Sankore.Modules.Customers.Features.Clients.RevealSensitiveField;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class RevealSensitiveFieldEndpoint
{
    public static IEndpointRouteBuilder MapRevealSensitiveField(this IEndpointRouteBuilder app)
    {
        app.MapPost("{clientId:guid}/reveal", Handle)
            .WithName("RevealSensitiveField")
            .WithSummary("Reveal one encrypted field of a client in clear text")
            .WithDescription(
                "Decrypts and returns a SINGLE protected field. Every call writes a "
                + "SensitiveDataAccessLog row (actor, client, field, timestamp, correlation id) and is "
                + "audited, so each access is individually traceable. "
                + "field is one of IdentityDocumentNumber, DateOfBirth, DeclaredIncome, Phone, Email, "
                + "PostalAddress, RegistrationNumber, TaxIdNumber. For Phone, Email and PostalAddress, "
                + "pass contactPointId to choose among several; omit it to get the active primary one. "
                + "Beyond the tenant's reveal-limit-per-hour quota (per USER, rolling hour) the call "
                + "answers 429 REVEAL_RATE_LIMIT_EXCEEDED and raises a compliance alert. "
                + "A field with nothing stored answers 404 UNKNOWN_SENSITIVE_FIELD; an unknown or "
                + "out-of-perimeter client answers 404 CLIENT_NOT_FOUND. "
                + "The response is sent with Cache-Control: no-store and Pragma: no-cache — it must "
                + "never be cached, logged or replayed. "
                + "It is a POST rather than a GET precisely so that no proxy or browser history keeps "
                + "the value. Requires permission: customers:reveal_sensitive.")
            .RequireAuthorization(Permissions.CanRevealCustomerSensitive.Code)
            .Produces<RevealSensitiveFieldResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        RevealSensitiveFieldRequest req,
        HttpContext http,
        ISender sender,
        CancellationToken ct)
    {
        // Set on the response BEFORE dispatching, so the headers are present whatever the
        // outcome: a 429 or a 404 on this route is itself a fact worth not caching.
        http.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";

        var result = await sender.Send(
            new RevealSensitiveFieldCommand(clientId, req.Field, req.ContactPointId), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            CustomerErrors.ClientNotFound or CustomerErrors.UnknownSensitiveField =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            CustomerErrors.RevealRateLimitExceeded =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status429TooManyRequests),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest)
        };
    }
}

public sealed record RevealSensitiveFieldRequest(
    SensitiveField Field,
    Guid? ContactPointId);
