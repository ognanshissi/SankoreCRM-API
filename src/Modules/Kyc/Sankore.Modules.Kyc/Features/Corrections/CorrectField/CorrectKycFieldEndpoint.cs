namespace Sankore.Modules.Kyc.Features.Corrections.CorrectField;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class CorrectKycFieldEndpoint
{
    internal static IEndpointRouteBuilder MapCorrectKycField(this IEndpointRouteBuilder app)
    {
        app.MapPost("{kycFileId:guid}/corrections", Handle)
            .WithName("CorrectKycField")
            .WithSummary("Correct a field the OCR misread")
            .WithDescription(
                "Records the correction — field name, source, author, both values encrypted — "
                + "and re-scores the file without re-running the OCR pass that produced the error.")
            // "Collect and correct KYC data": the same agent who entered the file fixes its
            // readings. Clearing a duplicate flag is the act that needs its own permission.
            .RequireAuthorization(Permissions.CanManageKycFile.Code)
            .Produces<CorrectKycFieldResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid kycFileId,
        CorrectKycFieldRequest req,
        ISender sender,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var result = await sender.Send(new CorrectKycFieldCommand(
            KycFileId: kycFileId,
            FieldName: req.FieldName,
            Source: req.Source,
            NewValue: req.NewValue,
            // Never from the body: the author of a correction is whoever holds the token, and a
            // caller must not be able to attribute their override to someone else.
            CorrectedBy: currentUser.Id), ct);

        if (result.IsSuccess)
            return Results.Ok(new CorrectKycFieldResponse(
                result.Value.CorrectionId,
                result.Value.NewScore,
                result.Value.NewConfidenceLevel,
                result.Value.ScoreUnavailableCode));

        // 404 and never 403 for an absent file: a file of another tenant is simply absent, and
        // "forbidden" would confirm that a customer exists there.
        return result.Error is KycErrors.FileNotFound or CorrectionErrors.IdentityDocumentNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }
}

/// <param name="NewValue">
/// The value the agent read off the document. Carried in the body and never in the URL: a query
/// string lands in access logs and in the browser history.
/// </param>
internal sealed record CorrectKycFieldRequest(string FieldName, string Source, string NewValue);

/// <summary>
/// Echoes no value, not even the one just sent. The response of a correction is the proof it was
/// recorded and the score it produced — the content is read back through the reveal endpoint.
/// </summary>
internal sealed record CorrectKycFieldResponse(
    Guid CorrectionId,
    int? NewScore,
    string? NewConfidenceLevel,
    string? ScoreUnavailableCode);
