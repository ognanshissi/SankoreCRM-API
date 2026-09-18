namespace Sankore.Modules.Leads.Features.WithdrawConsent;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class WithdrawConsentEndpoint
{
    public static IEndpointRouteBuilder MapWithdrawConsent(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/consents/{consentId:guid}/withdraw", Handle)
            .WithName("WithdrawConsent")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanWithdrawConsent.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        Guid consentId,
        WithdrawConsentRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var withdrawnBy = http.User.GetUserId();

        var result = await sender.Send(new WithdrawConsentCommand(
            LeadId:      leadId,
            ConsentId:   consentId,
            WithdrawnBy: withdrawnBy,
            Reason:      req.Reason), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "LEAD_NOT_FOUND" or "CONSENT_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Withdraw consent failed", detail: result.Error, statusCode: 422);
        }

        return Results.NoContent();
    }
}

/// <param name="Reason">Optional free-text explanation for the withdrawal (e.g. "Prospect called to opt out").</param>
public sealed record WithdrawConsentRequest(string? Reason = null);
