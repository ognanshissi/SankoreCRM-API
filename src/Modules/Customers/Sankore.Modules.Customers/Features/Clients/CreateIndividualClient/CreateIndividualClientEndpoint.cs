namespace Sankore.Modules.Customers.Features.Clients.CreateIndividualClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class CreateIndividualClientEndpoint
{
    public static IEndpointRouteBuilder MapCreateIndividualClient(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("CreateIndividualClient")
            .WithSummary("Register an individual client")
            .WithDescription(
                "Creates an individual client in PendingKyc status and mints its client number "
                + "({AgencyCode}-{YYYY}-{Seq}). The identity document number, date of birth, declared "
                + "income, phone numbers, e-mail and address are encrypted at rest and are never "
                + "returned in clear text by any read endpoint. "
                + "Answers 409 with the candidate clients in two cases: "
                + "DUPLICATE_IDENTITY_DOCUMENT (hard block, not overridable) and "
                + "POSSIBLE_DUPLICATE_PHONE (warning — resubmit with confirmNoDuplicate=true to accept "
                + "a shared number). "
                + "Answers 400 with CLIENT_UNDER_MINIMUM_AGE when the applicant is younger than the "
                + "tenant's minimum-age setting, and AGENCY_OUT_OF_SCOPE when the agency is outside the "
                + "caller's perimeter. "
                + "Requires permission: customers:create.")
            .RequireAuthorization(Permissions.CanCreateCustomer.Code)
            .Produces<CreateClientResult>(StatusCodes.Status201Created)
            .Produces<CreateClientResult>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateIndividualClientRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateIndividualClientCommand(
            AgencyId: req.AgencyId,
            AdvisorUserId: req.AdvisorUserId,
            FirstName: req.FirstName,
            LastName: req.LastName,
            MaidenName: req.MaidenName,
            Gender: req.Gender,
            DateOfBirth: req.DateOfBirth,
            BirthPlace: req.BirthPlace,
            Nationality: req.Nationality,
            MaritalStatus: req.MaritalStatus,
            FatherName: req.FatherName,
            MotherName: req.MotherName,
            Profession: req.Profession,
            Employer: req.Employer,
            DeclaredIncome: req.DeclaredIncome,
            DeclaredIncomeCurrency: req.DeclaredIncomeCurrency,
            PreferredLanguage: req.PreferredLanguage,
            IdentityDocumentType: req.IdentityDocumentType,
            IdentityDocumentNumber: req.IdentityDocumentNumber,
            IdentityDocumentIssuedOn: req.IdentityDocumentIssuedOn,
            IdentityDocumentExpiresOn: req.IdentityDocumentExpiresOn,
            PhoneNumbers: req.PhoneNumbers ?? [],
            Email: req.Email,
            Address: req.Address,
            ConfirmNoDuplicate: req.ConfirmNoDuplicate), ct);

        if (result.IsFailure)
            return Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);

        var payload = result.Value;

        // The two duplicate outcomes are successful RESULTS carrying a payload (the
        // existing clients) — they become 409 so the caller can branch on the status
        // code while still reading the candidates from the body.
        return payload.Outcome switch
        {
            CreateClientOutcome.Created =>
                Results.Created($"/api/v1/clients/{payload.ClientId}", payload),
            _ => Results.Json(payload, statusCode: StatusCodes.Status409Conflict)
        };
    }
}

/// <summary>
/// HTTP body of <c>POST api/v1/clients</c>. Separate from the command so the wire shape
/// can tolerate an omitted collection (<c>phoneNumbers: null</c>) while the command
/// itself always holds a list.
/// </summary>
public sealed record CreateIndividualClientRequest(
    Guid AgencyId,
    Guid? AdvisorUserId,
    string FirstName,
    string LastName,
    string? MaidenName,
    Gender Gender,
    DateOnly DateOfBirth,
    string? BirthPlace,
    string Nationality,
    MaritalStatus? MaritalStatus,
    string? FatherName,
    string? MotherName,
    string? Profession,
    string? Employer,
    decimal? DeclaredIncome,
    string? DeclaredIncomeCurrency,
    string? PreferredLanguage,
    IdentityDocumentType IdentityDocumentType,
    string IdentityDocumentNumber,
    DateOnly? IdentityDocumentIssuedOn,
    DateOnly? IdentityDocumentExpiresOn,
    IReadOnlyList<string>? PhoneNumbers,
    string? Email,
    PostalAddressInput? Address,
    bool ConfirmNoDuplicate);
