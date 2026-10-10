namespace Sankore.Modules.Leads.Features.CustomerLink.AttachLeadToCustomer;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

internal static class AttachLeadToCustomerEndpoint
{
    public static IEndpointRouteBuilder MapAttachLeadToCustomer(this IEndpointRouteBuilder app)
    {
        // lead:update rather than a permission of its own. Attaching IS an update to the lead, the
        // agency perimeter already decides which leads a user can reach, and a brand-new permission
        // would 401 every role until somebody re-seeded and re-logged in — which the module's
        // existing commands do not ask of anyone.
        app.MapPut("{leadId:guid}/customer", Handle)
            .WithName("AttachLeadToCustomer")
            .WithTags("Leads")
            .WithSummary("Attach a lead to an existing customer (BNF-B-22)")
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
        Guid leadId, AttachLeadToCustomerRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new AttachLeadToCustomerCommand(
            LeadId:            leadId,
            CustomerId:        req.CustomerId,
            Reason:            req.Reason,
            ExpectedUpdatedAt: req.ExpectedUpdatedAt), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "LEAD_NOT_FOUND"     => Results.NotFound(new { error = result.Error }),
                "CUSTOMER_NOT_FOUND" => Results.NotFound(new { error = result.Error }),
                "CONFLICT"           => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(
                    title: "Customer attachment failed", detail: result.Error, statusCode: 422)
            };
    }
}

/// <param name="Reason">Why this lead belongs to this customer. Required and audited.</param>
public sealed record AttachLeadToCustomerRequest(
    Guid CustomerId,
    string Reason,
    DateTimeOffset? ExpectedUpdatedAt = null);
