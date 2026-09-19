namespace Sankore.Modules.Leads.Features.SetIntentLevel;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

public static class SetIntentLevelEndpoint
{
    public static IEndpointRouteBuilder MapSetIntentLevel(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/intent-level", GetHandle)
            .WithName("GetLeadIntentLevel")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadIntentLevelDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapPut("{leadId:guid}/intent-level", PutHandle)
            .WithName("SetLeadIntentLevel")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> GetHandle(
        Guid leadId,
        LeadsDbContext db,
        CancellationToken ct)
    {
        var lead = await db.Leads
            .AsNoTracking()
            .Where(l => l.Id == leadId)
            .Select(l => new { l.Id, l.IntentLevel, l.Score, l.UpdatedAt })
            .FirstOrDefaultAsync(ct);

        return lead is null
            ? Results.NotFound()
            : Results.Ok(new LeadIntentLevelDto(lead.Id, lead.IntentLevel.ToString(), lead.Score, lead.UpdatedAt));
    }

    private static async Task<IResult> PutHandle(
        Guid leadId,
        SetIntentLevelRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new SetIntentLevelCommand(leadId, req.IntentLevel), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Intent level update failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record SetIntentLevelRequest(LeadIntentLevel IntentLevel);

/// <summary>Focused response for the GET /leads/{id}/intent-level endpoint.</summary>
public sealed record LeadIntentLevelDto(
    Guid LeadId,
    /// <summary>HOT / WARM / COLD / UNKNOWN</summary>
    string IntentLevel,
    int Score,
    DateTimeOffset UpdatedAt);
