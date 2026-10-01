namespace Sankore.Modules.Kyc.Features.Files.GetKycFile;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetKycFileEndpoint
{
    internal static IEndpointRouteBuilder MapGetKycFile(this IEndpointRouteBuilder app)
    {
        app.MapGet("{kycFileId:guid}", HandleById)
            .WithName("GetKycFile")
            .WithSummary("Read a KYC file by id")
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycFileDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapGet("by-customer/{customerId:guid}", HandleByCustomer)
            .WithName("GetKycFileByCustomer")
            .WithSummary("Read the open KYC file of a customer")
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycFileDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> HandleById(Guid kycFileId, ISender sender, CancellationToken ct)
        => Respond(await sender.Send(new GetKycFileQuery(kycFileId, null), ct));

    private static async Task<IResult> HandleByCustomer(Guid customerId, ISender sender, CancellationToken ct)
        => Respond(await sender.Send(new GetKycFileQuery(null, customerId), ct));

    private static IResult Respond(Result<KycFileDto> result)
        => result.IsSuccess
            ? Results.Ok(result.Value)
            // 404 and never 403: a file belonging to another tenant is simply absent, and telling
            // a caller "forbidden" would confirm that a customer exists there.
            : Results.NotFound(new { error = result.Error });
}
