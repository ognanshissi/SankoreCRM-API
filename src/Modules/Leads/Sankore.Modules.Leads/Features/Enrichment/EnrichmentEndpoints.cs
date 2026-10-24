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
            .WithSummary("Complete the principal's identity")
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

    /// <param name="projectId">
    /// Optional: the project a client file would be opened for — its product's policy applies.
    /// </param>
    private static async Task<IResult> GetCompleteness(
        Guid leadId, Guid? projectId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetLeadCompletenessQuery(leadId, projectId), ct);
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
            SourceActivityId:  req.SourceActivityId,
            ExpectedUpdatedAt: req.ExpectedUpdatedAt,
            Neighbourhood:     req.Neighbourhood,
            Landmark:          req.Landmark,
            NameVerified:      req.NameVerified), ct);

        // 200 with the recomputed report, not 204: the screen that saved a field wants to know
        // what is still missing, and it must learn it from the same calculator the conversion uses.
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);
    }

    private static IResult Problem(string? error) => error switch
    {
        "LEAD_NOT_FOUND"
        or EnrichLeadHandler.ActivityNotFound
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
    Guid? SourceActivityId = null,
    DateTimeOffset? ExpectedUpdatedAt = null,
    /// <summary>Quartier.</summary>
    string? Neighbourhood = null,
    /// <summary>Point de repère. Encrypted at rest.</summary>
    string? Landmark = null,
    /// <summary>True to record that an agent checked the spelling of the name.</summary>
    bool NameVerified = false);
