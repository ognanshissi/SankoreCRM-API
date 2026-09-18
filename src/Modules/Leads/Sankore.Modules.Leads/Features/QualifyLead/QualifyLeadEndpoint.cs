namespace Sankore.Modules.Leads.Features.QualifyLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class QualifyLeadEndpoint
{
    public static IEndpointRouteBuilder MapQualifyLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/qualify", Handle)
            .WithName("QualifyLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces<QualifyLeadResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        QualifyLeadRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var qualifiedBy = http.User.GetUserId();

        var result = await sender.Send(new QualifyLeadCommand(
            LeadId:      leadId,
            QualifiedBy: qualifiedBy,
            Score:       req.Score,
            TriggerEvent: req.TriggerEvent ?? "MANUAL_QUALIFICATION",
            TemplateId:  req.TemplateId,
            Answers:     req.Answers), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "LEAD_NOT_FOUND" or "QUALIFICATION_TEMPLATE_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Qualification failed", detail: result.Error, statusCode: 422);
        }

        return Results.Ok(result.Value);
    }
}

/// <summary>
/// Scoring paths (evaluated in priority order):
/// 1. TemplateId + Answers → weighted form scoring; response saved.
/// 2. Score → explicit override.
/// 3. Neither → auto-scored from lead attributes.
/// </summary>
public sealed record QualifyLeadRequest(
    /// <summary>ID of the qualification template to use.</summary>
    Guid? TemplateId = null,
    /// <summary>Answers to each template question; required when TemplateId is set.</summary>
    IReadOnlyList<QualificationAnswerInput>? Answers = null,
    /// <summary>Explicit override score (0-100). Ignored when TemplateId is provided.</summary>
    int? Score = null,
    string? TriggerEvent = null);
