namespace Sankore.Modules.Customers.Features.Clients.UpdateClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class UpdateClientEndpoint
{
    public static IEndpointRouteBuilder MapUpdateClient(this IEndpointRouteBuilder app)
    {
        app.MapPatch("{clientId:guid}", Handle)
            .WithName("UpdateClient")
            .WithSummary("Update the non-sensitive fields of a client")
            .WithDescription(
                "Partial update of profession, employer, marital status, declared income and "
                + "preferred language. A null field is left unchanged. "
                + "expectedVersion must carry the version read on the detail endpoint: a mismatch "
                + "answers 409 CONCURRENCY_CONFLICT rather than overwriting a concurrent edit. "
                + "An archived or merged client answers 409 CLIENT_READ_ONLY. A client that does not "
                + "exist OR lies outside the caller's agency perimeter answers 404 CLIENT_NOT_FOUND — "
                + "the two cases are deliberately indistinguishable. "
                + "Names, identity documents and addresses are NOT editable here: use "
                + "PATCH clients/{clientId}/sensitive, which requires a motive. "
                + "Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
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
        UpdateClientRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateClientCommand(
            ClientId: clientId,
            ExpectedVersion: req.ExpectedVersion,
            Profession: req.Profession,
            Employer: req.Employer,
            MaritalStatus: req.MaritalStatus,
            DeclaredIncome: req.DeclaredIncome,
            DeclaredIncomeCurrency: req.DeclaredIncomeCurrency,
            PreferredLanguage: req.PreferredLanguage), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error switch
        {
            CustomerErrors.ClientNotFound =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
            CustomerErrors.ConcurrencyConflict or CustomerErrors.ClientReadOnly =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
            CustomerErrors.AgencyOutOfScope =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest)
        };
    }
}

public sealed record UpdateClientRequest(
    uint ExpectedVersion,
    string? Profession,
    string? Employer,
    MaritalStatus? MaritalStatus,
    decimal? DeclaredIncome,
    string? DeclaredIncomeCurrency,
    string? PreferredLanguage);
