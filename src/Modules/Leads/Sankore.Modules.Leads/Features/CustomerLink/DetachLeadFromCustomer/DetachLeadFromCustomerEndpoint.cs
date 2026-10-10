namespace Sankore.Modules.Leads.Features.CustomerLink.DetachLeadFromCustomer;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

internal static class DetachLeadFromCustomerEndpoint
{
    public static IEndpointRouteBuilder MapDetachLeadFromCustomer(this IEndpointRouteBuilder app)
    {
        // POST .../detach rather than DELETE on the sub-resource, for two reasons that point the
        // same way. Minimal APIs refuse to INFER a body on DELETE — the host fails to start with
        // "Body was inferred but the method does not allow inferred body parameters" — and the
        // reason is mandatory (BNF-B-22), so it has to travel in a body. Even with an explicit
        // [FromBody] it would be a DELETE whose body some proxies strip, and the symptom of that
        // is a confusing 400 on the one field the caller did send.
        app.MapPost("{leadId:guid}/customer/detach", Handle)
            .WithName("DetachLeadFromCustomer")
            .WithTags("Leads")
            .WithSummary("Detach a lead from the customer it was attached to (BNF-B-22)")
            .RequireAuthorization(Permissions.CanUpdateLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId, DetachLeadFromCustomerRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DetachLeadFromCustomerCommand(
            LeadId:            leadId,
            Reason:            req.Reason,
            ExpectedUpdatedAt: req.ExpectedUpdatedAt), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "LEAD_NOT_FOUND"                => Results.NotFound(new { error = result.Error }),
                "LEAD_NOT_LINKED_TO_CUSTOMER"   => Results.NotFound(new { error = result.Error }),
                "CONFLICT"                      => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(
                    title: "Customer detachment failed", detail: result.Error, statusCode: 422)
            };
    }
}

public sealed record DetachLeadFromCustomerRequest(
    string Reason,
    DateTimeOffset? ExpectedUpdatedAt = null);
