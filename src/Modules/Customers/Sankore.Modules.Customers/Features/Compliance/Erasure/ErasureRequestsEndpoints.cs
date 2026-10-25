namespace Sankore.Modules.Customers.Features.Compliance.Erasure;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Erasure on request (data-purge lot 2): a person asks to be forgotten, one employee records it,
/// ANOTHER decides. Reading and recording need <c>privacy:erasure:request</c>; deciding needs
/// <c>privacy:erasure:approve</c>.
/// </summary>
public static class ErasureRequestsEndpoints
{
    public static IEndpointRouteBuilder MapErasureRequestsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("erasure-requests").WithTags("Erasure Requests");
        var request = Permissions.CanRequestErasure.Code;
        var approve = Permissions.CanApproveErasure.Code;

        group.MapGet("eligibility", async (ErasureSubjectType subjectType, Guid subjectId, ISender sender, CancellationToken ct)
                => Reply(await sender.Send(new GetErasureEligibilityQuery(subjectType, subjectId), ct)))
            .WithName("GetErasureEligibility")
            .WithSummary("Can this person be forgotten now — and if not, why and from when")
            .RequireAuthorization(request)
            .Produces<ErasureEligibilityDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi().WithTenantHeader();

        group.MapGet("", async (ErasureRequestStatus? status, int? page, int? pageSize, ISender sender, CancellationToken ct)
                => Reply(await sender.Send(new ListErasureRequestsQuery(status, page ?? 1, pageSize ?? 25), ct)))
            .WithName("ListErasureRequests")
            .WithSummary("Erasure requests, pending ones first")
            .RequireAuthorization(request)
            .Produces<PagedResult<ErasureRequestDto>>()
            .WithOpenApi().WithTenantHeader();

        group.MapGet("{requestId:guid}", async (Guid requestId, ISender sender, CancellationToken ct)
                => Reply(await sender.Send(new GetErasureRequestQuery(requestId), ct)))
            .WithName("GetErasureRequest")
            .RequireAuthorization(request)
            .Produces<ErasureRequestDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi().WithTenantHeader();

        group.MapPost("", async (CreateErasureRequestBody body, ISender sender, CancellationToken ct)
                => Reply(await sender.Send(new CreateErasureRequestCommand(body.SubjectType, body.SubjectId, body.Reason, body.Evidence), ct)))
            .WithName("CreateErasureRequest")
            .WithSummary("Record that a person asked to be forgotten")
            .WithDescription(
                "The reason and the evidence outlive the erasure, like the audit entry: they say why " +
                "and on what basis (« courrier reçu le 12/10 »), never who. ERASURE_ALREADY_REQUESTED " +
                "when an open request exists for the person, ERASURE_ALREADY_DONE when it was erased.")
            .RequireAuthorization(request)
            .Produces<ErasureRequestDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi().WithTenantHeader();

        group.MapPost("{requestId:guid}/approve", async (Guid requestId, DecideErasureRequestBody body, ISender sender, CancellationToken ct)
                => Reply(await sender.Send(new ApproveErasureRequestCommand(requestId, body.Note), ct)))
            .WithName("ApproveErasureRequest")
            .WithSummary("Approve: erase now, or restrict the client until its legal retention ends")
            .WithDescription(
                "Never by the person who recorded the request (ERASURE_FOUR_EYES). A prospect is erased " +
                "at once; a client still under its legal retention (LBC/FT) is RESTRICTED — no prospection, " +
                "no contact — and erased by a daily job on the day it becomes possible.")
            .RequireAuthorization(approve)
            .Produces<ErasureRequestDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi().WithTenantHeader();

        group.MapPost("{requestId:guid}/reject", async (Guid requestId, DecideErasureRequestBody body, ISender sender, CancellationToken ct)
                => Reply(await sender.Send(new RejectErasureRequestCommand(requestId, body.Note ?? ""), ct)))
            .WithName("RejectErasureRequest")
            .WithSummary("Reject the request, with the reason")
            .RequireAuthorization(approve)
            .Produces<ErasureRequestDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi().WithTenantHeader();

        return app;
    }

    private static IResult Reply<T>(Result<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : result.Error switch
        {
            CustomerErrors.ErasureNotFound or CustomerErrors.ErasureSubjectNotFound
                => Results.NotFound(new { error = result.Error }),
            CustomerErrors.ErasureAlreadyRequested or CustomerErrors.ErasureAlreadyDone
                or CustomerErrors.ErasureNotPending or CustomerErrors.ErasureFourEyes
                => Results.Conflict(new { error = result.Error }),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),
        };
}

public sealed record CreateErasureRequestBody(ErasureSubjectType SubjectType, Guid SubjectId, string Reason, string Evidence);

public sealed record DecideErasureRequestBody(string? Note);
