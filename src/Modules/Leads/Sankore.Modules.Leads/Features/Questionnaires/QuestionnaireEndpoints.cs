namespace Sankore.Modules.Leads.Features.Questionnaires;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Questionnaires.GetQuestionnaires;
using Sankore.Modules.Leads.Features.Questionnaires.RecordHealthConsent;
using Sankore.Modules.Leads.Features.Questionnaires.SubmitResponse;
using Sankore.Shared.Kernel;

/// <summary>Per-person questionnaires and health consent (BNF-B-07/08/09).</summary>
internal static class QuestionnaireEndpoints
{
    public static IEndpointRouteBuilder MapQuestionnaireEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/questionnaires", GetQuestionnaires)
            .WithName("GetLeadQuestionnaires")
            .WithTags("Leads")
            .WithSummary("Questionnaires owed and answered by each person of a lead")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadQuestionnairesDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/questionnaires/responses", SubmitResponse)
            .WithName("SubmitLeadQuestionnaireResponse")
            .WithTags("Leads")
            .WithSummary("Record one person's answers to a questionnaire")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

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

    private static async Task<IResult> GetQuestionnaires(
        Guid leadId, ISender sender, HttpContext http, CancellationToken ct)
    {
        // The health gate, decided HERE from the caller's claims — never inside the handler, so an
        // authorization reviewer finds it where they look for it (BNF-B-09).
        var canReadHealth = http.User.HasClaim("permission", Permissions.CanReadLeadHealth.Code);

        var result = await sender.Send(
            new GetLeadQuestionnairesQuery(leadId, canReadHealth), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);
    }

    private static async Task<IResult> SubmitResponse(
        Guid leadId, SubmitQuestionnaireResponseRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SubmitQuestionnaireResponseCommand(
            LeadId:            leadId,
            TemplateId:        req.TemplateId,
            SubjectType:       req.SubjectType,
            SubjectId:         req.SubjectId,
            Answers:           req.Answers,
            RespondentId:      req.RespondentId,
            ExpectedUpdatedAt: req.ExpectedUpdatedAt), ct);

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

public sealed record SubmitQuestionnaireResponseRequest(
    Guid TemplateId,
    /// <summary>"Principal" | "HouseholdMember".</summary>
    string SubjectType,
    Guid SubjectId,
    IReadOnlyList<QuestionnaireAnswerInput> Answers,
    /// <summary>Required for a minor; must be the principal or the spouse (BNF-B-08).</summary>
    Guid? RespondentId = null,
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record RecordHealthConsentRequest(
    string SubjectType,
    Guid SubjectId,
    /// <summary>A <c>ConsentChannel</c> name.</summary>
    string Channel,
    /// <summary>Where the evidence lives. Required (BNF-B-09).</summary>
    string ProofReference,
    Guid? GivenById = null);

public sealed record RecordHealthConsentResponse(Guid ConsentId);
