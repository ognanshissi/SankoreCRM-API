namespace Sankore.Modules.Notifications.Features.SmsTemplates;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Reading and customising the SMS bodies.
///
/// <para>
/// <b>Three slices, not the six the email templates have, and the missing ones are missing on
/// purpose.</b> There is no create-from-scratch and no activate: an SMS template key only matters
/// if some code sends it, so a tenant inventing a key would author a message nothing will ever
/// deliver. What an institution actually wants is to change the WORDING of a message this platform
/// already sends — its own name in a one-time code, its own tone — and that is fork-then-edit.
/// </para>
///
/// <para>
/// The group is <c>sms-templates</c> beside <c>email-templates</c>, same shape, so an
/// administrator who has used one recognises the other.
/// </para>
/// </summary>
internal static class SmsTemplatesEndpoints
{
    internal static IEndpointRouteBuilder MapSmsTemplatesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("sms-templates").WithTags("SMS Templates");

        group.MapGet("", ListAsync)
            .WithName("ListSmsTemplates")
            .WithSummary("The SMS bodies available to this tenant")
            .WithDescription(
                "Both the platform defaults and the tenant's own forks, with the segment count of "
                + "each — which is what the tenant is billed per message, and the number that "
                + "doubles at 161 characters. Requires notification:template:read.")
            .RequireAuthorization(Permissions.CanReadEmailTemplates.Code)
            .Produces<IReadOnlyList<SmsTemplateDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{templateKey}/{locale}/fork", ForkAsync)
            .WithName("ForkSmsTemplate")
            .WithSummary("Take a mutable tenant copy of a platform SMS body")
            .WithDescription(
                "A system template cannot be edited in place — every tenant reads it. Forking "
                + "creates a tenant-scoped copy at version 1 which then takes precedence for this "
                + "tenant only. Requires notification:template:manage.")
            .RequireAuthorization(Permissions.CanManageEmailTemplates.Code)
            .Produces<SmsTemplateDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("{templateKey}/{locale}", UpdateAsync)
            .WithName("UpdateSmsTemplate")
            .WithSummary("Edit the tenant's own SMS body")
            .WithDescription(
                "Writes a new version and retires the previous one, so what a customer was sent "
                + "last month stays readable. The response carries the segment count: a body that "
                + "crosses 160 GSM-7 characters — or gains a single accented character, which "
                + "drops the budget to 70 — costs twice as much per message. Requires "
                + "notification:template:manage.")
            .RequireAuthorization(Permissions.CanManageEmailTemplates.Code)
            .Accepts<UpdateSmsTemplateRequest>("application/json")
            .Produces<SmsTemplateDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> ListAsync(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListSmsTemplatesQuery(), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> ForkAsync(
        string templateKey, string locale, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ForkSmsTemplateCommand(templateKey, locale), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error == SmsTemplateErrors.NotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> UpdateAsync(
        string templateKey,
        string locale,
        [FromBody] UpdateSmsTemplateRequest body,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new UpdateSmsTemplateCommand(templateKey, locale, body.Body), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error == SmsTemplateErrors.NotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }
}

/// <param name="Body">The new message, as a Scriban template.</param>
internal sealed record UpdateSmsTemplateRequest(string Body);

/// <param name="SegmentCount">
/// What this body costs per message. Returned on every read and every write because it is the one
/// consequence of an edit that is invisible in the text — adding "é" to a 100-character message
/// takes it from one segment to two.
/// </param>
/// <param name="IsSystem">
/// A platform default, readable by every tenant and editable by none. Fork it first.
/// </param>
/// <param name="IsTenantOverride">
/// This row belongs to the calling tenant and takes precedence over the platform default of the
/// same key and locale.
/// </param>
public sealed record SmsTemplateDto(
    Guid Id,
    string TemplateKey,
    string Locale,
    int Version,
    string Body,
    int SegmentCount,
    bool IsActive,
    bool IsSystem,
    bool IsTenantOverride,
    DateTimeOffset CreatedAt);

internal static class SmsTemplateErrors
{
    public const string NotFound = "SMS_TEMPLATE_NOT_FOUND";

    /// <summary>A platform default cannot be edited in place — every tenant reads it.</summary>
    public const string NotForkable = "SMS_TEMPLATE_NOT_A_SYSTEM_TEMPLATE";

    /// <summary>Already forked: the tenant has its own copy and should edit that.</summary>
    public const string AlreadyForked = "SMS_TEMPLATE_ALREADY_FORKED";

    public const string BodyRequired = "SMS_TEMPLATE_BODY_REQUIRED";
}
