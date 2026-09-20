namespace Sankore.Modules.Leads.Features.LogActivity;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public static class LogActivityEndpoint
{
    public static IEndpointRouteBuilder MapLogActivity(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/activities", Handle)
            .WithName("LogLeadActivity")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanLogLeadActivity.Code)
            .Produces<LogActivityResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        LogActivityRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new LogActivityCommand(
                LeadId:              leadId,
                Type:                req.Type,
                Subject:             req.Subject,
                PerformedBy:         req.PerformedBy,
                Notes:               req.Notes,
                ScheduledAt:         req.ScheduledAt,
                DurationMinutes:     req.DurationMinutes,
                Outcome:             req.Outcome,
                AttachmentsJson:     req.AttachmentsJson,
                CtiCallReference:    req.CtiCallReference,
                IsSystemGenerated:   req.IsSystemGenerated,
                VisitLatitude:       req.VisitLatitude,
                VisitLongitude:      req.VisitLongitude,
                VisitPhotoReference: req.VisitPhotoReference),
            ct);

        return result.IsSuccess
            ? Results.Created($"leads/{leadId}/activities/{result.Value!.ActivityId}", result.Value)
            : result.Error is "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Log activity failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record LogActivityRequest(
    ActivityType Type,
    string Subject,
    Guid PerformedBy,
    string? Notes = null,
    DateTimeOffset? ScheduledAt = null,
    int? DurationMinutes = null,
    ActivityOutcome? Outcome = null,
    string? AttachmentsJson = null,
    string? CtiCallReference = null,
    bool IsSystemGenerated = false,
    double? VisitLatitude = null,
    double? VisitLongitude = null,
    string? VisitPhotoReference = null);
