namespace Sankore.Modules.Leads.Features.Household;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.Household.AddHouseholdMember;
using Sankore.Modules.Leads.Features.Household.GetHousehold;
using Sankore.Modules.Leads.Features.Household.RemoveHouseholdMember;
using Sankore.Modules.Leads.Features.Household.SetPepStatus;
using Sankore.Modules.Leads.Features.Household.UpdateHouseholdMember;
using Sankore.Shared.Kernel;

/// <summary>
/// The household and PEP endpoints of a lead (BNF-B-03, BNF-B-05).
/// </summary>
internal static class HouseholdEndpoints
{
    public static IEndpointRouteBuilder MapHouseholdEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/household", GetHousehold)
            .WithName("GetLeadHousehold")
            .WithTags("Leads")
            .WithSummary("The spouse, children and PEP exposure of a lead")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadHouseholdDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapPost("{leadId:guid}/household/members", AddMember)
            .WithName("AddLeadHouseholdMember")
            .WithTags("Leads")
            .WithSummary("Add a spouse or a child to a lead's household")
            .RequireAuthorization(Permissions.CanManageLeadHousehold.Code)
            .Produces<AddHouseholdMemberResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        app.MapPut("{leadId:guid}/household/members/{memberId:guid}", UpdateMember)
            .WithName("UpdateLeadHouseholdMember")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageLeadHousehold.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        // POST .../remove and not DELETE: the reason is mandatory (BNF-B-03) so it has to travel
        // in a body, and minimal APIs refuse to infer a body on DELETE — the host fails to start.
        // The same shape as POST leads/{id}/customer/detach.
        app.MapPost("{leadId:guid}/household/members/{memberId:guid}/remove", RemoveMember)
            .WithName("RemoveLeadHouseholdMember")
            .WithTags("Leads")
            .WithSummary("Remove a household member (logical — the row is kept)")
            .RequireAuthorization(Permissions.CanManageLeadHousehold.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        app.MapPut("{leadId:guid}/pep", SetPrincipalPep)
            .WithName("SetLeadPrincipalPepStatus")
            .WithTags("Leads")
            .WithSummary("Declare the principal's politically-exposed-person status")
            .RequireAuthorization(Permissions.CanManageLeadHousehold.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapPut("{leadId:guid}/household/members/{memberId:guid}/pep", SetMemberPep)
            .WithName("SetLeadHouseholdMemberPepStatus")
            .WithTags("Leads")
            .WithSummary("Declare a household member's PEP status (overrides the inherited link)")
            .RequireAuthorization(Permissions.CanManageLeadHousehold.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> GetHousehold(
        Guid leadId, ISender sender, HttpContext http, CancellationToken ct)
    {
        // The PEP detail gate lives HERE, where an authorization reviewer looks — not inside the
        // handler. The exposure FLAG travels either way (BNF-B-05).
        var canReadPep = http.User.HasClaim("permission", Permissions.CanReadLeadPep.Code);

        var result = await sender.Send(new GetHouseholdQuery(leadId, canReadPep), ct);

        // Through the shared mapping, so the read answers 403 on LEAD_NOT_YOURS exactly as the
        // writes do — a 404 would send an agent hunting for a lead that is simply somebody else's.
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);
    }

    private static async Task<IResult> AddMember(
        Guid leadId, AddHouseholdMemberRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new AddHouseholdMemberCommand(
            LeadId:               leadId,
            Type:                 req.Type,
            Identity:             req.Identity,
            SpouseUnionType:      req.SpouseUnionType,
            UsesPrincipalAddress: req.UsesPrincipalAddress,
            Pep:                  req.Pep,
            ExpectedUpdatedAt:    req.ExpectedUpdatedAt), ct);

        return result.IsSuccess
            ? Results.Created(
                $"/api/v1/leads/{leadId}/household/members/{result.Value}",
                new AddHouseholdMemberResponse(result.Value))
            : Problem(result.Error);
    }

    private static async Task<IResult> UpdateMember(
        Guid leadId, Guid memberId, UpdateHouseholdMemberRequest req,
        ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateHouseholdMemberCommand(
            LeadId:               leadId,
            MemberId:             memberId,
            Identity:             req.Identity,
            SpouseUnionType:      req.SpouseUnionType,
            UsesPrincipalAddress: req.UsesPrincipalAddress,
            ExpectedUpdatedAt:    req.ExpectedUpdatedAt), ct);

        return result.IsSuccess ? Results.NoContent() : Problem(result.Error);
    }

    private static async Task<IResult> RemoveMember(
        Guid leadId, Guid memberId, RemoveHouseholdMemberRequest req,
        ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RemoveHouseholdMemberCommand(
            LeadId:            leadId,
            MemberId:          memberId,
            Reason:            req.Reason,
            ExpectedUpdatedAt: req.ExpectedUpdatedAt), ct);

        return result.IsSuccess ? Results.NoContent() : Problem(result.Error);
    }

    private static async Task<IResult> SetPrincipalPep(
        Guid leadId, SetPepStatusRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SetPrincipalPepStatusCommand(
            LeadId: leadId, Pep: req.Pep, ExpectedUpdatedAt: req.ExpectedUpdatedAt), ct);

        return result.IsSuccess ? Results.NoContent() : Problem(result.Error);
    }

    private static async Task<IResult> SetMemberPep(
        Guid leadId, Guid memberId, SetPepStatusRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new SetHouseholdMemberPepStatusCommand(
            LeadId: leadId, MemberId: memberId, Pep: req.Pep,
            ExpectedUpdatedAt: req.ExpectedUpdatedAt), ct);

        return result.IsSuccess ? Results.NoContent() : Problem(result.Error);
    }

    /// <summary>
    /// One place for the status mapping, because five endpoints share the same error vocabulary.
    /// <c>LEAD_NOT_YOURS</c> is a 403 and not a 404: unlike a client's existence (BNF-B-23), a
    /// lead's existence inside the caller's own agency perimeter is not a secret — and a 404 would
    /// send an agent hunting for a lead that is simply somebody else's.
    /// </summary>
    private static IResult Problem(string? error) => error switch
    {
        "LEAD_NOT_FOUND" or "HOUSEHOLD_MEMBER_NOT_FOUND"
            => Results.NotFound(new { error }),
        LeadAccessGuard.Forbidden
            => Results.Problem(title: "Not your lead", detail: error, statusCode: 403),
        "CONFLICT"
            => Results.Conflict(new { error }),
        HouseholdPolicyResolver.PolicyUnavailable
        or HouseholdWriteContext.ComplianceUnavailable
            => Results.Problem(title: "Household not available", detail: error, statusCode: 422),
        _   => Results.Problem(title: "Household update failed", detail: error, statusCode: 422),
    };
}

public sealed record AddHouseholdMemberRequest(
    /// <summary>"Spouse" | "Child".</summary>
    string Type,
    PersonIdentityDto Identity,
    /// <summary>"Married" | "CivilUnion". Required for a spouse, forbidden for a child.</summary>
    string? SpouseUnionType = null,
    /// <summary>True by default (BNF-B-03): the address is resolved from the principal at read time.</summary>
    bool UsesPrincipalAddress = true,
    /// <summary>Omit to inherit the principal's FamilyMember link (BNF-B-05).</summary>
    PepStatusDto? Pep = null,
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record AddHouseholdMemberResponse(Guid MemberId);

public sealed record UpdateHouseholdMemberRequest(
    PersonIdentityDto Identity,
    string? SpouseUnionType = null,
    bool UsesPrincipalAddress = true,
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record RemoveHouseholdMemberRequest(
    string Reason,
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record SetPepStatusRequest(
    PepStatusDto Pep,
    DateTimeOffset? ExpectedUpdatedAt = null);
