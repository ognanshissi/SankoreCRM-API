namespace Sankore.Modules.Leads.Features.Questionnaires;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Questionnaires.ProjectQuestionnaires;
using Sankore.Modules.Leads.Features.Questionnaires.RecordHealthConsent;
using Sankore.Shared.Kernel;

/// <summary>Project questionnaires and per-person health consent (BNF-B-07/08/09).</summary>
internal static class QuestionnaireEndpoints
{
    public static IEndpointRouteBuilder MapQuestionnaireEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/health-consents", RecordConsent)
            .WithName("RecordLeadHealthConsent")
            .WithTags("Leads")
            .WithSummary("Record a person's explicit consent to health-data processing")
            .RequireAuthorization(Permissions.CanRecordConsent.Code)
            .Produces<RecordHealthConsentResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    /// <summary>
    /// The questionnaires of a PROJECT: the file's and each person's, as its product asks them.
    /// Mapped on the opportunities group.
    /// </summary>
    public static RouteGroupBuilder MapProjectQuestionnaireEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("{id:guid}/questionnaires", GetProjectQuestionnaires)
            .WithName("GetProjectQuestionnaires")
            .WithTags("Opportunities")
            .WithSummary("Questionnaires a project's product asks, of its file and of each person, with their answers")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<ProjectQuestionnairesDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        group.MapPost("{id:guid}/questionnaires/responses", SubmitProjectResponse)
            .WithName("SubmitProjectQuestionnaireResponse")
            .WithTags("Opportunities")
            .WithSummary("Record the answers to one of a project's questionnaires")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return group;
    }

    private static async Task<IResult> GetProjectQuestionnaires(
        Guid id, ISender sender, HttpContext http, CancellationToken ct)
    {
        // The health gate, decided HERE from the caller's claims — never inside the handler, so an
        // authorization reviewer finds it where they look for it (BNF-B-09).
        var canReadHealth = http.User.HasClaim("permission", Permissions.CanReadLeadHealth.Code);

        var result = await sender.Send(new GetProjectQuestionnairesQuery(id, canReadHealth), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);
    }

    private static async Task<IResult> SubmitProjectResponse(
        Guid id, SubmitProjectQuestionnaireResponseRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SubmitProjectQuestionnaireResponseCommand(
            ProjectId:    id,
            TemplateId:   req.TemplateId,
            SubjectType:  req.SubjectType,
            SubjectId:    req.SubjectId,
            Answers:      req.Answers,
            RespondentId: req.RespondentId), ct);

        return result.IsSuccess ? Results.NoContent() : Problem(result.Error);
    }

    private static async Task<IResult> RecordConsent(
        Guid leadId, RecordHealthConsentRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RecordHealthConsentCommand(
            LeadId:         leadId,
            SubjectType:    req.SubjectType,
            SubjectId:      req.SubjectId,
            Channel:        req.Channel,
            ProofReference: req.ProofReference,
            GivenById:      req.GivenById), ct);

        return result.IsSuccess
            ? Results.Created(
                $"/api/v1/leads/{leadId}/health-consents/{result.Value}",
                new RecordHealthConsentResponse(result.Value))
            : Problem(result.Error);
    }

    private static IResult Problem(string? error) => error switch
    {
        "LEAD_NOT_FOUND" or "TEMPLATE_NOT_FOUND" or "QUALIFICATION_SUBJECT_NOT_FOUND"
        or Opportunities.OpportunityErrors.NotFound
            => Results.NotFound(new { error }),
        LeadAccessGuard.Forbidden or HealthAnswerAccess.Forbidden
            => Results.Problem(title: "Not permitted", detail: error, statusCode: 403),
        "CONFLICT"
            => Results.Conflict(new { error }),
        HealthConsentGuard.Missing
        or HealthConsentGuard.RespondentRequired
        or HealthConsentGuard.RespondentInvalid
            => Results.Problem(title: "Consent or respondent missing", detail: error, statusCode: 422),
        _   => Results.Problem(title: "Questionnaire operation failed", detail: error, statusCode: 422),
    };
}

public sealed record RecordHealthConsentRequest(
    string SubjectType,
    Guid SubjectId,
    /// <summary>A <c>ConsentChannel</c> name.</summary>
    string Channel,
    /// <summary>Where the evidence lives. Required (BNF-B-09).</summary>
    string ProofReference,
    Guid? GivenById = null);

public sealed record RecordHealthConsentResponse(Guid ConsentId);
