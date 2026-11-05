namespace Sankore.Modules.Leads.Features.Opportunities.CreditSubmission;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <param name="State">
/// <c>NotApproved</c>, <c>Incomplete</c>, or a <see cref="LoanApplicationState"/> name.
/// </param>
/// <param name="Missing">What the application lacks, by label, before it can leave.</param>
public sealed record CreditSubmissionDto(
    string State,
    string? Reference,
    string? Message,
    DateTimeOffset? At,
    IReadOnlyList<string> Missing);

internal sealed record GetCreditSubmissionQuery(Guid ProjectId) : IRequest<Result<CreditSubmissionDto?>>;

/// <summary>
/// Where a credit file stands with the core banking, for the project's page: read from the state of
/// the project and of the command. Null for a project that is not a credit file.
/// </summary>
internal sealed class GetCreditSubmissionHandler(
    LeadsDbContext db,
    ProjectTemplateResolver templates,
    ILeadsModule leads,
    IIntegrationModule integration,
    LeadVisibility? visibility = null)
    : IRequestHandler<GetCreditSubmissionQuery, Result<CreditSubmissionDto?>>
{
    public async Task<Result<CreditSubmissionDto?>> Handle(GetCreditSubmissionQuery query, CancellationToken ct)
    {
        var project = await CreditFiles.VisibleAsync(db, visibility, query.ProjectId, ct);
        if (project is null) return Result.Fail<CreditSubmissionDto?>("OPPORTUNITY_NOT_FOUND");

        var template = await templates.ForProjectAsync(project, ct);
        if (!CreditFileTemplate.IsCreditFile(template)) return Result.Ok<CreditSubmissionDto?>(null);

        var facts = await leads.GetCreditApplicationAsync(project.TenantId, project.Id, ct);
        var missing = new List<string>();
        if (facts?.ClientId is null) missing.Add("Fiche client");
        if (facts?.ProductLineId is null) missing.Add("Produit du catalogue");
        if (facts?.Amount is null) missing.Add("Montant");
        if (facts?.TermMonths is null) missing.Add("Durée");

        var status = await integration.CoreBanking.GetLoanApplicationStatusAsync(project.Id, ct);

        // Once queued, the command tells the story; before that, what stands in its way.
        var state = status.State switch
        {
            LoanApplicationState.NotRequested or LoanApplicationState.NoConnection or LoanApplicationState.NotSupported
                when !CreditFiles.IsApproved(project, template) => "NotApproved",
            LoanApplicationState.NotRequested when missing.Count > 0 => "Incomplete",
            _ => status.State.ToString(),
        };

        return Result.Ok<CreditSubmissionDto?>(new CreditSubmissionDto(state, status.Reference, status.Message, status.At, missing));
    }
}

/// <summary>
/// Sends again a credit file that left no command behind: incomplete at its approval, or approved
/// before the core banking was connected. A file already queued stays as it is: the command is
/// idempotent on the project.
/// </summary>
internal sealed record SendCreditSubmissionCommand(Guid ProjectId) : IRequest<Result>, ICommand;

internal sealed class SendCreditSubmissionHandler(
    LeadsDbContext db,
    ProjectTemplateResolver templates,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    LeadVisibility? visibility = null)
    : IRequestHandler<SendCreditSubmissionCommand, Result>
{
    public async Task<Result> Handle(SendCreditSubmissionCommand command, CancellationToken ct)
    {
        var project = await CreditFiles.VisibleAsync(db, visibility, command.ProjectId, ct);
        if (project is null) return Result.Fail("OPPORTUNITY_NOT_FOUND");

        var template = await templates.ForProjectAsync(project, ct);
        if (!CreditFileTemplate.IsCreditFile(template) || !CreditFiles.IsApproved(project, template))
            return Result.Fail("CREDIT_FILE_NOT_APPROVED");

        await publisher.PublishAsync(new CreditFileApprovedEvent(project.TenantId, project.Id), ct);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal static class CreditFiles
{
    public static Task<Opportunity?> VisibleAsync(LeadsDbContext db, LeadVisibility? visibility, Guid projectId, CancellationToken ct)
    {
        var projects = visibility?.Apply(db.Opportunities, db.Leads) ?? db.Opportunities;
        return projects.AsNoTracking().FirstOrDefaultAsync(o => o.Id == projectId, ct);
    }

    public static bool IsApproved(Opportunity project, ResolvedProjectTemplate template)
    {
        if (project.Stage == OpportunityStage.ClosedLost) return false;
        if (project.Stage == OpportunityStage.ClosedWon) return true;

        var index = template.Steps.ToList().FindIndex(s => s.Id == project.StepId);
        return index >= 0 && ProjectLeadConversion.Approves(template, index);
    }

    public static IEndpointRouteBuilder MapCreditSubmission(this IEndpointRouteBuilder group)
    {
        group.MapGet("{opportunityId:guid}/credit-submission", async (Guid opportunityId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetCreditSubmissionQuery(opportunityId), ct);
                if (result.IsFailure) return Results.NotFound();
                return result.Value is { } dto ? Results.Ok(dto) : Results.NoContent();
            })
            .WithName("GetProjectCreditSubmission")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<CreditSubmissionDto>()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("{opportunityId:guid}/credit-submission", async (Guid opportunityId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new SendCreditSubmissionCommand(opportunityId), ct);
                if (result.IsSuccess) return Results.NoContent();
                return result.Error == "OPPORTUNITY_NOT_FOUND"
                    ? Results.NotFound()
                    : Results.Problem(detail: result.Error, statusCode: StatusCodes.Status409Conflict);
            })
            .WithName("SendProjectCreditSubmission")
            .WithTags("Opportunities")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        return group;
    }
}
