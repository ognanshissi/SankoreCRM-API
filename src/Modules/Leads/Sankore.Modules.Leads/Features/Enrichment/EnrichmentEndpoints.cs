namespace Sankore.Modules.Leads.Features.Enrichment;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Enrichment.EnrichLead;
using Sankore.Modules.Leads.Features.Enrichment.GetCompleteness;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

/// <summary>Enrichment after dispatching and the completeness report (BNF-B-02, BNF-B-04).</summary>
internal static class EnrichmentEndpoints
{
    public static IEndpointRouteBuilder MapEnrichmentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/completeness", GetCompleteness)
            .WithName("GetLeadCompleteness")
            .WithTags("Leads")
            .WithSummary("What is still missing from a lead's file, per person")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadCompletenessDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/enrich", Enrich)
            .WithName("EnrichLead")
            .WithTags("Leads")
            .WithSummary("Complete the principal's identity and resolve the product line")
            .RequireAuthorization(Permissions.CanUpdateLead.Code)
            .Produces<LeadCompletenessDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> GetCompleteness(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetLeadCompletenessQuery(leadId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);
    }

    private static async Task<IResult> Enrich(
        Guid leadId, EnrichLeadRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new EnrichLeadCommand(
            LeadId:            leadId,
            FirstName:         req.FirstName,
            LastName:          req.LastName,
            UsageName:         req.UsageName,
            BirthDate:         req.BirthDate,
            Gender:            req.Gender,
            AddressLine:       req.AddressLine,
            PostalCode:        req.PostalCode,
            City:              req.City,
            CountryCode:       req.CountryCode,
            ProductLineId:     req.ProductLineId,
            ClearProductLine:  req.ClearProductLine,
            SourceActivityId:  req.SourceActivityId,
            ExpectedUpdatedAt: req.ExpectedUpdatedAt), ct);

        // 200 with the recomputed report, not 204: the screen that saved a field wants to know
        // what is still missing, and it must learn it from the same calculator the conversion uses.
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);
    }

    private static IResult Problem(string? error) => error switch
    {
        "LEAD_NOT_FOUND"
        or EnrichLeadHandler.ActivityNotFound
        or EnrichLeadHandler.ProductLineNotFound
            => Results.NotFound(new { error }),
        LeadAccessGuard.Forbidden
            => Results.Problem(title: "Not permitted", detail: error, statusCode: 403),
        "CONFLICT"
            => Results.Conflict(new { error }),
        HouseholdPolicyResolver.PolicyUnavailable
        or HouseholdWriteContext.ComplianceUnavailable
            // A dependency was unreachable, not a bad request. 503 so a client retries rather
            // than showing the agent a validation error they cannot act on.
            => Results.Problem(title: "A dependency is unavailable", detail: error, statusCode: 503),
        _   => Results.Problem(title: "Enrichment failed", detail: error, statusCode: 422),
    };
}

/// <summary>
/// The enrichment payload (BNF-B-02). Every field optional; null leaves the stored value alone and
/// blank clears it — see <see cref="EnrichLeadCommand"/>.
/// </summary>
public sealed record EnrichLeadRequest(
    string? FirstName = null,
    string? LastName = null,
    string? UsageName = null,
    DateOnly? BirthDate = null,
    /// <summary>A <c>LeadGender</c> name.</summary>
    string? Gender = null,
    string? AddressLine = null,
    string? PostalCode = null,
    string? City = null,
    /// <summary>ISO 3166-1 alpha-2.</summary>
    string? CountryCode = null,
    Guid? ProductLineId = null,
    bool ClearProductLine = false,
    Guid? SourceActivityId = null,
    DateTimeOffset? ExpectedUpdatedAt = null);
