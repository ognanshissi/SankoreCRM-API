namespace Sankore.Modules.Kyc.Features.Files.CreateKycFile;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class CreateKycFileEndpoint
{
    internal static IEndpointRouteBuilder MapCreateKycFile(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("CreateKycFile")
            .WithSummary("Open the KYC file of a customer")
            .WithDescription(
                "Normally automatic: M01 and M13 open the file when a client is created or a lead "
                + "converted. This endpoint exists for the customer who slipped through — it is "
                + "idempotent and answers 200 with the existing file rather than refusing.")
            .RequireAuthorization(Permissions.CanManageKycFile.Code)
            .Produces<CreateKycFileResponse>(StatusCodes.Status201Created)
            .Produces<CreateKycFileResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateKycFileRequest req,
        ISender sender,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateKycFileCommand(
            TenantId: currentUser.TenantId,
            CustomerId: req.CustomerId,
            Channel: req.Channel ?? KycChannel.Agency,
            InitiatedBy: currentUser.Id,
            VigilanceLevel: req.VigilanceLevel ?? KycVigilanceLevel.Standard), ct);

        if (result.IsFailure)
            return Results.BadRequest(new { error = result.Error });

        var response = new CreateKycFileResponse(result.Value.KycFileId, result.Value.AlreadyExisted);

        // 200 rather than 201 when the file was already there: the caller's intent is satisfied,
        // but nothing was created and a client that treats 201 as "I made this" must not be misled.
        return result.Value.AlreadyExisted
            ? Results.Ok(response)
            : Results.Created($"/api/v1/kyc-files/{result.Value.KycFileId}", response);
    }
}

internal sealed record CreateKycFileRequest(
    Guid CustomerId,
    KycChannel? Channel = null,
    KycVigilanceLevel? VigilanceLevel = null);

internal sealed record CreateKycFileResponse(Guid KycFileId, bool AlreadyExisted);
