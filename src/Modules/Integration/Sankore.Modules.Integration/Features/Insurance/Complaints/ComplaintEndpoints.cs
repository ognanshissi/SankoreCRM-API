namespace Sankore.Modules.Integration.Features.Insurance.Complaints;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The <i>mécanisme de traitement des réclamations</i> of the règlement
/// n° 01/CIMA/PCMA/CE/SG/2024.
///
/// <para>
/// <b>One route per act and no PUT.</b> Acknowledging, escalating, answering and closing are four
/// different things a regulator counts separately, and each earns its own audit row — which is
/// also the timestamped opposable record the 2024 text asks for, since <c>audit.entries</c> is
/// immutable at the database level. A single PATCH would collapse the four into one row saying
/// "something changed".
/// </para>
///
/// <para>
/// <b>There is no DELETE.</b> A complaint withdrawn by the customer is <c>Closed</c> with its
/// reason, not removed: the register is what the institution shows a supervisor, and a register
/// one can delete from is not a register.
/// </para>
/// </summary>
internal static class ComplaintEndpoints
{
    internal static IEndpointRouteBuilder MapComplaints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("integration/insurance/complaints").WithTags("Integration");

        group.MapPost(string.Empty, Record)
            .WithName("RecordInsuranceComplaint")
            .WithSummary("Record a customer complaint")
            .WithDescription(
                "The entry point of the complaints mechanism the 2024 CIMA regulation on "
                + "distribution by electronic means requires. Returns the reference the customer "
                + "quotes back (`REC-2026-000042`) and the two deadlines, both computed from when "
                + "the CUSTOMER complained — `receivedAt`, which may be days before an agent "
                + "typed it in — and STORED, so neither moves when an administrator changes the "
                + "configured period. What the customer said is encrypted; the subject is in "
                + "clear because it appears in lists and in a supervisor's report. "
                + "Requires permission: Ins.Complaint.Handle.")
            .RequireAuthorization(Permissions.CanHandleInsuranceComplaint.Code)
            .Produces<ComplaintDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet(string.Empty, List)
            .WithName("ListInsuranceComplaints")
            .WithSummary("The complaints register")
            .WithDescription(
                "`crmCustomerId` narrows to one customer, `openOnly` to what the institution "
                + "still owes an answer on, `overdueOnly` to the breached deadlines. What the "
                + "customer said is NOT returned — `hasBody` says it is on file; the ANSWER is, "
                + "in clear, because it is the institution's own position. "
                + "Requires permission: Ins.Complaint.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceComplaint.Code)
            .Produces<IReadOnlyList<ComplaintDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("report", Report)
            .WithName("GetInsuranceComplaintReport")
            .WithSummary("The mechanism's figures over a window")
            .WithDescription(
                "What a supervisor asks for: how many arrived, through which channel, about what, "
                + "how many were upheld, and how many deadlines were missed. A breach counts "
                + "whether the step was done late OR is still not done past its date — otherwise "
                + "a mechanism would improve its figures by never acknowledging. The duration is "
                + "a MEDIAN: one complaint that took two years would drag a mean past every "
                + "honest figure. "
                + "Requires permission: Ins.Complaint.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceComplaint.Code)
            .Produces<ComplaintReportDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{complaintId:guid}", Get)
            .WithName("GetInsuranceComplaint")
            .WithSummary("One complaint")
            .WithDescription("Requires permission: Ins.Complaint.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceComplaint.Code)
            .Produces<ComplaintDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{complaintId:guid}/acknowledgement", Acknowledge)
            .WithName("AcknowledgeInsuranceComplaint")
            .WithSummary("Tell the customer the complaint is in hand")
            .WithDescription(
                "The first of the two deadlines the mechanism is judged on. Idempotent, and it "
                + "keeps the FIRST instant: re-stamping it would let a late acknowledgement be "
                + "made to look punctual by sending a second one. "
                + "Requires permission: Ins.Complaint.Handle.")
            .RequireAuthorization(Permissions.CanHandleInsuranceComplaint.Code)
            .Produces<ComplaintDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{complaintId:guid}/escalation", Escalate)
            .WithName("EscalateInsuranceComplaint")
            .WithSummary("Hand the complaint to the insurer or to the supervisor")
            .WithDescription(
                "NOT terminal: the institution still owes the customer an answer and the "
                + "resolution deadline keeps running, which is why escalation is a status and not "
                + "a closure. "
                + "Requires permission: Ins.Complaint.Handle.")
            .RequireAuthorization(Permissions.CanHandleInsuranceComplaint.Code)
            .Produces<ComplaintDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{complaintId:guid}/resolution", Resolve)
            .WithName("ResolveInsuranceComplaint")
            .WithSummary("Answer the complaint")
            .WithDescription(
                "The answer text is REQUIRED including for a complaint found unjustified: "
                + "\"we considered your complaint and rejected it\" with no reason is exactly the "
                + "answer this mechanism exists to prevent, and it is the one a supervisor asks "
                + "to see. `wasUpheld` is separate from the status because \"how many did we "
                + "uphold\" is the question a mechanism is judged on. "
                + "Requires permission: Ins.Complaint.Handle.")
            .RequireAuthorization(Permissions.CanHandleInsuranceComplaint.Code)
            .Produces<ComplaintDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{complaintId:guid}/closure", Close)
            .WithName("CloseInsuranceComplaint")
            .WithSummary("Close a complaint without answering it")
            .WithDescription(
                "For one withdrawn by the customer or outside the mechanism's scope. "
                + "Deliberately NOT the same status as an answer: a report counting a withdrawal "
                + "as treated would overstate how many complaints the institution handled, so "
                + "`wasUpheld` stays null. There is no DELETE — a register one can delete from is "
                + "not a register. "
                + "Requires permission: Ins.Complaint.Handle.")
            .RequireAuthorization(Permissions.CanHandleInsuranceComplaint.Code)
            .Produces<ComplaintDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Record(
        RecordComplaintRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new RecordComplaintCommand(
            CrmCustomerId: body.CrmCustomerId,
            Channel: body.Channel,
            Category: body.Category,
            Subject: body.Subject,
            Body: body.Body,
            ConnectionId: body.ConnectionId,
            PolicyId: body.PolicyId,
            ClaimId: body.ClaimId,
            ReceivedAt: body.ReceivedAt), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> List(
        ISender sender,
        CancellationToken ct,
        Guid? crmCustomerId = null,
        bool openOnly = false,
        bool overdueOnly = false)
    {
        var result = await sender.Send(
            new ListComplaintsQuery(crmCustomerId, openOnly, overdueOnly), ct);

        return Results.Ok(result.Value);
    }

    private static async Task<IResult> Report(
        DateOnly from, DateOnly to, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ComplaintReportQuery(from, to), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> Get(
        Guid complaintId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetComplaintQuery(complaintId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static Task<IResult> Acknowledge(
        Guid complaintId, ISender sender, CancellationToken ct)
        => AdvanceAsync(sender, complaintId, ComplaintAction.Acknowledge, null, false, ct);

    private static Task<IResult> Escalate(
        Guid complaintId, EscalateComplaintRequest body, ISender sender, CancellationToken ct)
        => AdvanceAsync(
            sender, complaintId, ComplaintAction.Escalate, body.EscalatedTo, false, ct);

    private static Task<IResult> Resolve(
        Guid complaintId, ResolveComplaintRequest body, ISender sender, CancellationToken ct)
        => AdvanceAsync(
            sender, complaintId, ComplaintAction.Resolve, body.Resolution, body.WasUpheld, ct);

    private static Task<IResult> Close(
        Guid complaintId, CloseComplaintRequest body, ISender sender, CancellationToken ct)
        => AdvanceAsync(sender, complaintId, ComplaintAction.Close, body.Reason, false, ct);

    /// <summary>
    /// The four acts share one translation of the aggregate's refusals, so a new act cannot ship
    /// with a 400 where its siblings answer 409.
    /// </summary>
    private static async Task<IResult> AdvanceAsync(
        ISender sender,
        Guid complaintId,
        ComplaintAction action,
        string? text,
        bool wasUpheld,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new AdvanceComplaintCommand(complaintId, action, text, wasUpheld), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        var error = result.Error!;

        if (error == ComplaintErrors.NotFound)
            return Results.NotFound(new { error });

        // The request is well-formed and the STATE refuses it: the complaint is already answered
        // or withdrawn, and re-answering would rewrite what the customer was told.
        if (error == ComplaintErrors.AlreadyClosed)
            return Results.Conflict(new { error });

        return Results.BadRequest(new { error });
    }
}
