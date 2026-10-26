namespace Sankore.Modules.Customers.Features.Complaints;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The client's complaints register (2026-10-26) — every product. An insurance complaint under the
/// règlement CIMA keeps its own register under <c>integration/insurance/complaints</c>.
/// </summary>
public static class ClientComplaintsEndpoints
{
    public static IEndpointRouteBuilder MapClientComplaintsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("clients/{clientId:guid}/complaints").WithTags("Client Complaints");

        group.MapGet("", List)
            .WithName("ListClientComplaints")
            .WithSummary("The client's complaints, newest first (without what the client wrote)")
            .RequireAuthorization(Permissions.CanReadClientComplaints.Code)
            .Produces<IReadOnlyList<ClientComplaintSummaryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{complaintId:guid}", Get)
            .WithName("GetClientComplaint")
            .WithSummary("One complaint, with what the client said and the answer given, decrypted")
            .RequireAuthorization(Permissions.CanReadClientComplaints.Code)
            .Produces<ClientComplaintDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("", Record)
            .WithName("FileClientComplaint")
            .WithSummary("Record a complaint; the deadlines come from the tenant settings")
            .WithDescription(
                "channel: Branch | Telephone | Email | WhatsApp | Post. category: Other | Credit | "
                + "Savings | FeesAndPricing | PaymentMeans | Service | DataProtection. The complaint "
                + "is assigned to the client's advisor (else its creator, else the agency manager), "
                + "who receives a task, a notification and an e-mail.")
            .RequireAuthorization(Permissions.CanManageClientComplaints.Code)
            .Produces<FileClientComplaintResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{complaintId:guid}/acknowledgement", Acknowledge)
            .WithName("AcknowledgeClientComplaint")
            .WithSummary("Record that the client was told the complaint is in hand")
            .RequireAuthorization(Permissions.CanManageClientComplaints.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{complaintId:guid}/resolution", Resolve)
            .WithName("ResolveClientComplaint")
            .WithSummary("Record the answer given to the client (also acknowledges an unacknowledged complaint)")
            .RequireAuthorization(Permissions.CanManageClientComplaints.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{complaintId:guid}/closure", Close)
            .WithName("CloseClientComplaint")
            .WithSummary("Close without an answer — withdrawn by the client, or outside the register's scope")
            .RequireAuthorization(Permissions.CanManageClientComplaints.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> List(Guid clientId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListClientComplaintsQuery(clientId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : ToProblem(result.Error);
    }

    private static async Task<IResult> Get(Guid clientId, Guid complaintId, HttpContext http, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetClientComplaintQuery(clientId, complaintId), ct);
        if (result.IsFailure) return ToProblem(result.Error);

        // Decrypted free text about a person: never kept by a browser or a proxy.
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> Record(Guid clientId, RecordClientComplaintRequest req, ISender sender, CancellationToken ct)
    {
        if (!Enum.TryParse<ClientComplaintChannel>(req.Channel, ignoreCase: true, out var channel)
            || !Enum.TryParse<ClientComplaintCategory>(req.Category, ignoreCase: true, out var category))
            return Results.Problem("channel or category is not a known value", statusCode: StatusCodes.Status400BadRequest);

        var result = await sender.Send(new FileClientComplaintCommand(
            clientId, channel, category, req.Subject ?? string.Empty, req.Description ?? string.Empty, req.HoldingId), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/clients/{clientId}/complaints/{result.Value!.ComplaintId}", result.Value)
            : ToProblem(result.Error);
    }

    private static async Task<IResult> Acknowledge(Guid clientId, Guid complaintId, ISender sender, CancellationToken ct)
        => NoContentOr(await sender.Send(new AcknowledgeClientComplaintCommand(clientId, complaintId), ct));

    private static async Task<IResult> Resolve(Guid clientId, Guid complaintId, ResolveClientComplaintRequest req, ISender sender, CancellationToken ct)
        => NoContentOr(await sender.Send(new ResolveClientComplaintCommand(clientId, complaintId, req.Response ?? string.Empty), ct));

    private static async Task<IResult> Close(Guid clientId, Guid complaintId, CloseClientComplaintRequest req, ISender sender, CancellationToken ct)
        => NoContentOr(await sender.Send(new CloseClientComplaintCommand(clientId, complaintId, req.Reason ?? string.Empty), ct));

    private static IResult NoContentOr(Result result) => result.IsSuccess ? Results.NoContent() : ToProblem(result.Error);

    private static IResult ToProblem(string? error) => error switch
    {
        CustomerErrors.ClientNotFound or CustomerErrors.ComplaintNotFound => Results.NotFound(new { error }),
        CustomerErrors.ClientReadOnly or CustomerErrors.ComplaintInvalidTransition => Results.Conflict(new { error }),
        _ => Results.UnprocessableEntity(new { error }),
    };
}

public sealed record RecordClientComplaintRequest(
    string Channel,
    string Category,
    string? Subject,
    string? Description,
    Guid? HoldingId);

public sealed record ResolveClientComplaintRequest(string? Response);

public sealed record CloseClientComplaintRequest(string? Reason);
