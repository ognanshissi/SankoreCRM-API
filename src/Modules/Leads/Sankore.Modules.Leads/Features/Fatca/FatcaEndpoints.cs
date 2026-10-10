namespace Sankore.Modules.Leads.Features.Fatca;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Fatca.GetDeclarations;
using Sankore.Modules.Leads.Features.Fatca.SubmitDeclaration;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Shared.Kernel;

/// <summary>FATCA indicia and self-certification (BNF-B-11).</summary>
internal static class FatcaEndpoints
{
    public static IEndpointRouteBuilder MapFatcaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/fatca", GetDeclarations)
            .WithName("GetLeadFatcaDeclarations")
            .WithTags("Leads")
            .WithSummary("The FATCA position of each person of a lead")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadFatcaDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/fatca/declarations", Submit)
            .WithName("SubmitLeadFatcaDeclaration")
            .WithTags("Leads")
            .WithSummary("Record a person's FATCA self-certification (supersedes the previous one)")
            .RequireAuthorization(Permissions.CanManageLeadHousehold.Code)
            .Produces<SubmitFatcaDeclarationResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> GetDeclarations(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetFatcaDeclarationsQuery(leadId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);
    }

    private static async Task<IResult> Submit(
        Guid leadId, SubmitFatcaDeclarationRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SubmitFatcaDeclarationCommand(
            LeadId:                  leadId,
            SubjectType:             req.SubjectType,
            SubjectId:               req.SubjectId,
            Indicia:                 req.Indicia,
            Documentation:           req.Documentation,
            UsTin:                   req.UsTin,
            SelfCertificationDocRef: req.SelfCertificationDocRef,
            EvidenceDocRef:          req.EvidenceDocRef,
            SignedAt:                req.SignedAt,
            RefusalReason:           req.RefusalReason), ct);

        return result.IsSuccess
            ? Results.Created(
                $"/api/v1/leads/{leadId}/fatca/declarations/{result.Value}",
                new SubmitFatcaDeclarationResponse(result.Value))
            : Problem(result.Error);
    }

    private static IResult Problem(string? error) => error switch
    {
        "LEAD_NOT_FOUND" => Results.NotFound(new { error }),
        LeadAccessGuard.Forbidden
            => Results.Problem(title: "Not your lead", detail: error, statusCode: 403),
        // 422 and not 400: the request is well formed, the institution simply has no FATCA
        // obligation — which is a state of the world, not a mistake in the payload.
        FatcaSubjectPlanner.NotEnabled
        or FatcaSubjectPlanner.SettingsUnavailable
        or "FATCA_SUBJECT_NOT_IN_SCOPE"
            => Results.Problem(title: "FATCA not applicable", detail: error, statusCode: 422),
        _   => Results.Problem(title: "FATCA declaration failed", detail: error, statusCode: 422),
    };
}

public sealed record SubmitFatcaDeclarationRequest(
    /// <summary>"Principal" | "HouseholdMember".</summary>
    string SubjectType,
    Guid SubjectId,
    FatcaIndiciaInput Indicia,
    /// <summary>"None" | "FormW9" | "FormW8Ben" | "Refused".</summary>
    string Documentation,
    /// <summary>Required for FormW9. Encrypted at rest; never echoed back.</summary>
    string? UsTin = null,
    string? SelfCertificationDocRef = null,
    /// <summary>Required for FormW8Ben.</summary>
    string? EvidenceDocRef = null,
    /// <summary>When the person signed. Required unless the documentation is a refusal.</summary>
    DateTimeOffset? SignedAt = null,
    string? RefusalReason = null);

public sealed record SubmitFatcaDeclarationResponse(Guid DeclarationId);
