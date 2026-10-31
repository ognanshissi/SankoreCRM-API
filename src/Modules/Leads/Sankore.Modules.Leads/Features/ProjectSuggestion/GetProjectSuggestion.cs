namespace Sankore.Modules.Leads.Features.ProjectSuggestion;

using System.Transactions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// « Créer un projet pour ce produit ? » on the lead (2026-10-31, product owner): the catalogue
/// products its « produit souhaité » looks like (<see cref="ProductMatcher"/>), for the agent to open
/// the project in one click. Nothing when the lead is closed, named no product, or already has an
/// open project for that product — the proposal is for the project nobody opened yet.
/// A lead read: an agent asking about a lead they do not hold gets LEAD_NOT_FOUND
/// (<see cref="LeadVisibilityBehavior{TRequest,TResponse}"/>), like every other read of one lead.
/// </summary>
internal sealed record GetProjectSuggestionQuery(Guid LeadId) : IRequest<Result<ProjectSuggestionDto?>>, ILeadReadRequest;

public sealed record ProductSuggestionDto(
    string Code, string Name, string Category, int Confidence, ProductMatchBasis Basis);

public sealed record ProjectSuggestionDto(string InterestedProduct, IReadOnlyList<ProductSuggestionDto> Products);

internal sealed class GetProjectSuggestionHandler(LeadsDbContext db, IAdministrationModule administration)
    : IRequestHandler<GetProjectSuggestionQuery, Result<ProjectSuggestionDto?>>
{
    private static readonly LeadStatus[] Closed =
        [LeadStatus.Converted, LeadStatus.Archived, LeadStatus.Lost, LeadStatus.Disqualified];

    public async Task<Result<ProjectSuggestionDto?>> Handle(GetProjectSuggestionQuery query, CancellationToken ct)
    {
        // The agent's own leads are enforced by ILeadReadRequest, before this runs.
        var lead = await db.Leads.AsNoTracking()
            .Where(l => l.Id == query.LeadId)
            .Select(l => new { l.TenantId, l.Status, l.InterestedProduct })
            .FirstOrDefaultAsync(ct);
        if (lead is null) return Result.Fail<ProjectSuggestionDto?>("LEAD_NOT_FOUND");
        if (Closed.Contains(lead.Status)) return Result.Ok<ProjectSuggestionDto?>(null);

        IReadOnlyList<ProductSummary> catalogue;
        using (var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled))
        {
            catalogue = await administration.ListProductsAsync(lead.TenantId, null, ct);
            suppressed.Complete();
        }

        var matches = ProductMatcher.Rank(lead.InterestedProduct, catalogue);
        if (matches.Count == 0) return Result.Ok<ProjectSuggestionDto?>(null);

        // A product an open project already carries — by its line, or by its label — is not proposed again.
        var open = await db.Opportunities.AsNoTracking()
            .Where(o => o.LeadId == query.LeadId && o.Stage != OpportunityStage.ClosedWon && o.Stage != OpportunityStage.ClosedLost)
            .Select(o => new { o.Product, o.ProductLineId })
            .ToListAsync(ct);
        var openLabels = open.Select(o => ProductMatcher.Normalize(o.Product)).Where(p => p.Length > 0).ToHashSet();
        var openLines = open.Select(o => o.ProductLineId).OfType<Guid>().ToHashSet();

        var proposed = matches
            .Where(m => !openLines.Contains(m.Product.Id)
                     && !openLabels.Contains(ProductMatcher.Normalize(m.Product.Name))
                     && !openLabels.Contains(ProductMatcher.Normalize(m.Product.Code)))
            .Select(m => new ProductSuggestionDto(m.Product.Code, m.Product.Name, m.Product.Category, m.Confidence, m.Basis))
            .ToList();

        return Result.Ok<ProjectSuggestionDto?>(proposed.Count == 0 ? null : new ProjectSuggestionDto(lead.InterestedProduct!, proposed));
    }
}

internal static class GetProjectSuggestionEndpoint
{
    public static IEndpointRouteBuilder MapGetProjectSuggestion(this IEndpointRouteBuilder group)
    {
        group.MapGet("{leadId:guid}/project-suggestion", async (Guid leadId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetProjectSuggestionQuery(leadId), ct);
                if (result.IsFailure) return Results.NotFound();
                return result.Value is null ? Results.NoContent() : Results.Ok(result.Value);
            })
            .WithName("GetLeadProjectSuggestion")
            .WithTags("Leads")
            .WithSummary("Catalogue products the lead's « produit souhaité » looks like, to open a project for")
            .WithDescription(
                "Matched on the product's code, name (any order, a typo, part of it) or description, accents and case "
                + "folded; confidence 60–100, at most three. 204 when the lead is closed, named no product, nothing "
                + "matches, or an open project already carries the product. A suggestion only: the project's line is "
                + "still resolved exactly when it is created.")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<ProjectSuggestionDto>()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithTenantHeader();
        return group;
    }
}
