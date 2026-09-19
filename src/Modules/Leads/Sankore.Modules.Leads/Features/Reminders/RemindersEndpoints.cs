namespace Sankore.Modules.Leads.Features.Reminders;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Reminders.CompleteReminder;
using Sankore.Modules.Leads.Features.Reminders.CreateReminder;
using Sankore.Modules.Leads.Features.Reminders.DismissReminder;
using Sankore.Modules.Leads.Features.Reminders.ListReminders;
using Sankore.Modules.Leads.Features.Reminders.RescheduleReminder;
using Sankore.Shared.Kernel;

public static class RemindersEndpoints
{
    public static IEndpointRouteBuilder MapRemindersEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("{leadId:guid}/reminders");

        // GET leads/{leadId}/reminders
        group.MapGet("", ListReminders)
            .WithName("ListLeadReminders")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<ReminderDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST leads/{leadId}/reminders
        group.MapPost("", CreateReminder)
            .WithName("CreateLeadReminder")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageLeadReminders.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST leads/{leadId}/reminders/{reminderId}/complete
        group.MapPost("{reminderId:guid}/complete", CompleteReminder)
            .WithName("CompleteLeadReminder")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageLeadReminders.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST leads/{leadId}/reminders/{reminderId}/dismiss
        group.MapPost("{reminderId:guid}/dismiss", DismissReminder)
            .WithName("DismissLeadReminder")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageLeadReminders.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT leads/{leadId}/reminders/{reminderId}/reschedule
        group.MapPut("{reminderId:guid}/reschedule", RescheduleReminder)
            .WithName("RescheduleLeadReminder")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanManageLeadReminders.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListReminders(
        Guid leadId, ISender sender, CancellationToken ct,
        ReminderStatus? status = null)
    {
        var result = await sender.Send(new ListRemindersQuery(leadId, status), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> CreateReminder(
        Guid leadId,
        CreateReminderRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new CreateReminderCommand(leadId, req.Title, req.CreatedBy, req.DueAt, req.Notes), ct);

        return result.IsSuccess
            ? Results.Created($"leads/{leadId}/reminders/{result.Value}", result.Value)
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Create reminder failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> CompleteReminder(
        Guid leadId, Guid reminderId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CompleteReminderCommand(leadId, reminderId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "REMINDER_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Complete failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> DismissReminder(
        Guid leadId, Guid reminderId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DismissReminderCommand(leadId, reminderId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "REMINDER_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Dismiss failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> RescheduleReminder(
        Guid leadId, Guid reminderId,
        RescheduleReminderRequest req,
        ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new RescheduleReminderCommand(leadId, reminderId, req.NewDueAt), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "REMINDER_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Reschedule failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record CreateReminderRequest(
    string Title,
    Guid CreatedBy,
    DateTimeOffset DueAt,
    string? Notes = null);

public sealed record RescheduleReminderRequest(DateTimeOffset NewDueAt);
