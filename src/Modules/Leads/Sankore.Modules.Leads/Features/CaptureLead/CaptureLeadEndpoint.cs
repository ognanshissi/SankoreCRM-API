namespace Sankore.Modules.Leads.Features.CaptureLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class CaptureLeadEndpoint
{
    public static IEndpointRouteBuilder MapCaptureLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("", Handle)
            .WithName("CaptureLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanCaptureLead.Code)
            .Produces<CaptureLeadResult>(StatusCodes.Status201Created)
            .Produces<CaptureLeadResult>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CaptureLeadRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var tenantId = http.User.GetTenantId();

        // ── v1 / v2 reconciliation (BNF-B-18) ───────────────────────────────
        //
        // v1 sends FullName; v2 may send only the structured names. Resolved HERE rather than in
        // the handler, because it is a property of the WIRE contract and not of the domain: the
        // command has always required a name, and the aggregate already derives FullName from the
        // structured pair when both are present (BNF-B-01).
        var fullName = ResolveName(req);

        if (fullName is null)
        {
            // Refused before anything is created. A lead needs a name somebody can call out in a
            // branch, and inventing a placeholder would put "[sans nom]" on a dispatch queue.
            return Results.Problem(
                title: "Lead capture failed",
                detail: "NAME_REQUIRED: supply fullName, or firstName and lastName.",
                statusCode: 422);
        }

        // Health answers without their consent proof are refused BEFORE the lead is created
        // (BNF-B-09/B-18) — see CaptureQuestionnaire.ConsentProofReference. Checked here, where
        // the request is, rather than after a partial write.
        var health = CaptureHealthConsentCheck.Validate(
            req.Questionnaires, req.Household?.Count ?? 0);

        if (health.IsFailure)
        {
            return Results.Problem(
                title: "Lead capture failed", detail: health.Error, statusCode: 422);
        }

        var result = await sender.Send(new CaptureLeadCommand(
            TenantId:             tenantId,
            FullName:             fullName,
            PhoneNumber:          req.PhoneNumber,
            Source:               req.Source,
            InterestedProduct:    req.InterestedProduct,
            PreferredLanguage:    req.PreferredLanguage,
            Latitude:             req.Latitude,
            Longitude:            req.Longitude,
            PreferredAgencyId:    req.PreferredAgencyId,
            FirstName:            req.FirstName,
            LastName:             req.LastName,
            Email:                req.Email,
            Gender:               req.Gender ?? LeadGender.Unknown,
            DateOfBirth:          req.DateOfBirth,
            DesiredAmount:        req.DesiredAmount,
            DesiredCurrency:      req.DesiredCurrency,
            Campaign:             req.Campaign,
            Channel:              req.Channel,
            Comment:              req.Comment,
            ExternalReference:    req.ExternalReference,
            OwnerId:              req.OwnerId,
            AgencyId:             req.AgencyId,
            AgentCollectedLeadId: req.AgentCollectedLeadId,
            CompanyName:          req.CompanyName,
            CompanyEmail:         req.CompanyEmail,
            CompanyPhone:         req.CompanyPhone,
            Website:              req.Website,
            ProspectType:             req.ProspectType ?? LeadType.Individual,
            NationalId:               req.NationalId,
            CustomerReference:        req.CustomerReference,
            GateMode:                 req.GateMode ?? DuplicateGateMode.Block,
            MinConfidenceThreshold:   req.MinConfidenceThreshold ?? IdentityMatchScorer.MinConfidence,
            Force:                    req.Force,
            LeadSourceConfigId: req.LeadSourceConfigId,
            Household:          req.Household,
            Questionnaires:     req.Questionnaires,
            CustomFields:       req.CustomFields,
            EnforceFormRequirements: true), ct);

        if (!result.IsSuccess)
            return Results.Problem(title: "Lead capture failed", detail: result.Error, statusCode: 422);

        // Block mode with duplicates: lead was NOT created.
        if (result.Value.DuplicateDetected && result.Value.LeadId is null)
            return Results.Conflict(result.Value);

        // Warn mode with duplicates (or no duplicates): lead WAS created.
        return Results.Created($"/api/leads/{result.Value.LeadId}", result.Value);
    }

    /// <summary>
    /// v1's name, v2's derived one, or null when the request carries neither.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="LeadNameResolver"/>, which is the one rule the web ingest, the
    /// scheduled pull and its dry run share — see there for why a supplied full name wins.
    /// </remarks>
    internal static string? ResolveName(CaptureLeadRequest req)
        => LeadNameResolver.Resolve(req.FullName, req.FirstName, req.LastName);
}

/// <summary>
/// The capture payload (BNF-B-18). <b>Two shapes, one contract.</b>
///
/// <para>
/// <b>v1</b> sends <paramref name="FullName"/> and nothing structured — every web form and every
/// integration written before this evolution. It keeps working unchanged: the name is mapped
/// through as it always was.
/// </para>
///
/// <para>
/// <b>v2</b> sends <paramref name="FirstName"/> and <paramref name="LastName"/> separately, plus
/// an optional household and optional questionnaire answers, and may omit
/// <paramref name="FullName"/> entirely — it is derived. BNF-B-01 already made the structured
/// names win over the captured label on the aggregate, so this is the contract catching up with
/// the domain.
/// </para>
///
/// <para>
/// <b>One record and not two endpoints</b>, deliberately. The two shapes differ by which optional
/// fields are populated, not by their semantics, and a parallel <c>/capture/v2</c> would duplicate
/// twenty-eight fields that must stay identical — the duplicate drifting is a certainty, and the
/// day it drifts a lead captured through one route scores differently from the same lead captured
/// through the other. What v2 actually needed was for <paramref name="FullName"/> to stop being
/// mandatory, which is a relaxation: every v1 body remains valid.
/// </para>
/// </summary>
public sealed record CaptureLeadRequest(
    /// <summary>
    /// v1's whole name. OPTIONAL since v2: supply it, or supply
    /// <paramref name="FirstName"/> + <paramref name="LastName"/> and let it be derived. A request
    /// with neither is refused — a lead needs a name somebody can call out in a branch.
    /// </summary>
    string? FullName,
    string PhoneNumber,
    LeadSource Source,
    string InterestedProduct,
    string PreferredLanguage,
    double Latitude,
    double Longitude,
    Guid? PreferredAgencyId,
    string? FirstName = null,
    string? LastName = null,
    string? Email = null,
    LeadGender? Gender = null,
    DateOnly? DateOfBirth = null,
    decimal? DesiredAmount = null,
    string? DesiredCurrency = null,
    string? Campaign = null,
    LeadChannel? Channel = null,
    string? Comment = null,
    string? ExternalReference = null,
    Guid? OwnerId = null,
    Guid? AgencyId = null,
    Guid? AgentCollectedLeadId = null,
    string? CompanyName = null,
    string? CompanyEmail = null,
    string? CompanyPhone = null,
    string? Website = null,
    LeadType? ProspectType = null,
    string? NationalId = null,
    string? CustomerReference = null,
    /// <summary>
    /// Block (default): returns 409 when duplicates found; re-submit with Force=true to override.
    /// Warn: creates the lead and returns 201 with DuplicateDetected=true when duplicates found.
    /// </summary>
    DuplicateGateMode? GateMode = null,
    /// <summary>
    /// Minimum confidence score (0-100) for a candidate to trigger the gate. Defaults to 30.
    /// </summary>
    double? MinConfidenceThreshold = null,
    /// <summary>
    /// Set to true to bypass the duplicate gate entirely and force creation.
    /// </summary>
    bool Force = false,
    Guid? LeadSourceConfigId = null,
    /// <summary>
    /// The prospect's household, collected at capture (BNF-B-18). Optional, and almost always
    /// absent: a web form does not ask for a spouse. An agent on a tablet in front of a family
    /// does, and before this the household had to be recorded in a second request that could fail
    /// on its own.
    /// </summary>
    IReadOnlyList<CaptureHouseholdMember>? Household = null,
    /// <summary>
    /// Questionnaire answers collected at capture (BNF-B-18). A health questionnaire REQUIRES its
    /// consent proof — see <see cref="CaptureQuestionnaire.ConsentProofReference"/>.
    /// </summary>
    IReadOnlyList<CaptureQuestionnaire>? Questionnaires = null,
    /// <summary>
    /// Values of the tenant's custom lead fields (<c>GET forms/Lead</c>), keyed by field key. This
    /// endpoint also enforces the fields the tenant made required — native or custom.
    /// </summary>
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? CustomFields = null);

/// <summary>
/// One household member on a capture (BNF-B-18) — the same shape the household endpoints take, so
/// an agent's screen posts one object whichever route it uses.
/// </summary>
/// <param name="Type">"Spouse" | "Child".</param>
/// <param name="SpouseUnionType">"Married" | "CivilUnion". Required for a spouse, forbidden for a child.</param>
public sealed record CaptureHouseholdMember(
    string Type,
    string LastName,
    string FirstName,
    string? SpouseUnionType = null,
    DateOnly? BirthDate = null,
    string? Gender = null,
    string? AddressLine = null,
    string? PostalCode = null,
    string? City = null,
    string? CountryCode = null,
    bool UsesPrincipalAddress = true);

/// <summary>
/// One questionnaire answered at capture (BNF-B-18).
/// </summary>
/// <param name="ConsentProofReference">
/// Where the evidence of the person's consent to health-data processing is stored.
///
/// <para>
/// <b>REQUIRED when the template is a health questionnaire, and the capture is REFUSED without
/// it.</b> Not stored-and-ignored, and not silently dropped: a caller that sent health answers and
/// got a 201 would believe they were recorded. BNF-B-09 makes an explicit, evidenced consent the
/// condition of collecting health data at all, and a capture is no exception — it is in fact the
/// one place where the temptation to let it through is strongest.
/// </para>
/// </param>
/// <param name="SubjectIndex">
/// Which person the answers are about: <c>null</c> for the principal, otherwise the zero-based
/// index into <c>Household</c>. An INDEX and not an id, because the members do not exist yet — the
/// capture creates them — so there is nothing to point at.
/// </param>
public sealed record CaptureQuestionnaire(
    Guid TemplateId,
    IReadOnlyList<CaptureAnswer> Answers,
    string? ConsentProofReference = null,
    int? SubjectIndex = null);

public sealed record CaptureAnswer(Guid QuestionId, string Value);
