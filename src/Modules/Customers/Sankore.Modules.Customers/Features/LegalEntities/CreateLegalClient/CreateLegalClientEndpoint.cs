namespace Sankore.Modules.Customers.Features.LegalEntities.CreateLegalClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class CreateLegalClientEndpoint
{
    public static IEndpointRouteBuilder MapCreateLegalClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("legal", Handle)
            .WithName("CreateLegalClient")
            .WithSummary("Register a legal entity (company, association, cooperative)")
            .WithDescription(
                "Creates a legal client in status PendingKyc. The legal form must belong to the " +
                "tenant's active legal-form list (LEGAL_FORM_UNKNOWN otherwise). The registration " +
                "number (RCCM) and the tax id (NIF) are stored encrypted; the RCCM also carries a " +
                "blind index, so an RCCM already used inside the tenant answers 409 " +
                "DUPLICATE_REGISTRATION_NUMBER together with the existing client — the duplicate " +
                "check is an indexed equality and never decrypts anything. At least one contact " +
                "(phone or e-mail) is required. Requires permission: customers:create.")
            .RequireAuthorization(Permissions.CanCreateCustomer.Code)
            .Produces<CreateLegalClientResult>(StatusCodes.Status201Created)
            .Produces<CreateLegalClientResult>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateLegalClientRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateLegalClientCommand(
            AgencyId: req.AgencyId,
            AdvisorUserId: req.AdvisorUserId,
            LegalName: req.LegalName,
            LegalFormCode: req.LegalFormCode,
            RegistrationNumber: req.RegistrationNumber,
            TaxIdNumber: req.TaxIdNumber,
            IncorporationDate: req.IncorporationDate,
            HeadOfficeAddress: req.HeadOfficeAddress,
            PhoneNumbers: req.PhoneNumbers ?? [],
            Email: req.Email,
            PreferredLanguage: req.PreferredLanguage), ct);

        if (result.IsFailure)
        {
            // Out-of-scope agency is an authorization outcome, not a payload problem.
            return result.Error == CustomerErrors.AgencyOutOfScope
                ? Results.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden)
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        var value = result.Value;

        // A blocking duplicate is a successful result carrying a code: 409 WITH the body,
        // so the operator can jump straight to the existing company.
        return value.Outcome switch
        {
            CreateLegalClientOutcome.Created =>
                Results.Created($"/api/v1/clients/{value.ClientId}", value),
            _ => Results.Json(value, statusCode: StatusCodes.Status409Conflict),
        };
    }
}

public sealed record CreateLegalClientRequest(
    Guid AgencyId,
    Guid? AdvisorUserId,
    string LegalName,
    string LegalFormCode,
    string RegistrationNumber,
    string? TaxIdNumber,
    DateOnly IncorporationDate,
    PostalAddressInput HeadOfficeAddress,
    IReadOnlyList<string>? PhoneNumbers,
    string? Email,
    string? PreferredLanguage);
