namespace Sankore.Modules.Notifications.Features.SmsTemplates;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Modules.Notifications.Infrastructure.Sms;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// The three SMS template slices in one file.
///
/// <para>
/// A departure from the repo's one-folder-per-slice rule, and a deliberate one: each of these is a
/// query or a command of under thirty lines over the same two entities, and spreading them across
/// nine files would make the area harder to read rather than easier. The email templates have
/// their own folders because each carries a validator and a non-trivial handler; if any of these
/// grows one, it moves out.
/// </para>
/// </summary>
internal sealed record ListSmsTemplatesQuery : IRequest<Result<IReadOnlyList<SmsTemplateDto>>>;

internal sealed class ListSmsTemplatesHandler(
    NotificationsDbContext db, ICurrentUser currentUser)
    : IRequestHandler<ListSmsTemplatesQuery, Result<IReadOnlyList<SmsTemplateDto>>>
{
    public async Task<Result<IReadOnlyList<SmsTemplateDto>>> Handle(
        ListSmsTemplatesQuery request, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        // Both sets: the platform defaults every tenant reads, and this tenant's own forks.
        // SmsTemplate carries no query filter — its TenantId is nullable because a platform row
        // belongs to nobody — so the predicate is explicit here.
        var rows = await db.SmsTemplates
            .Where(t => t.TenantId == null || t.TenantId == tenantId)
            .OrderBy(t => t.TemplateKey)
            .ThenBy(t => t.Locale)
            .ThenByDescending(t => t.Version)
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<SmsTemplateDto>>(
            [.. rows.Select(t => Map(t, tenantId))]);
    }

    internal static SmsTemplateDto Map(SmsTemplate t, Guid tenantId) => new(
        t.Id,
        t.TemplateKey,
        t.Locale,
        t.Version,
        t.Body,
        // Computed on read rather than stored: the body is Scriban source, so the count is what
        // the UNRENDERED template costs — an estimate the administrator can act on, and the
        // honest one, since the rendered length depends on the data.
        SmsSegments.Count(t.Body),
        t.IsActive,
        t.IsSystem,
        IsTenantOverride: t.TenantId == tenantId,
        t.CreatedAt);
}

// ── Fork ────────────────────────────────────────────────────────────────────

internal sealed record ForkSmsTemplateCommand(string TemplateKey, string Locale)
    : IRequest<Result<SmsTemplateDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "SmsTemplate";
    public string? ResourceId => $"{TemplateKey}/{Locale}";
}

internal sealed class ForkSmsTemplateHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser,
    ILogger<ForkSmsTemplateHandler> logger)
    : IRequestHandler<ForkSmsTemplateCommand, Result<SmsTemplateDto>>
{
    public async Task<Result<SmsTemplateDto>> Handle(
        ForkSmsTemplateCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var locale = LanguageCode.Normalize(cmd.Locale);

        // Already forked: the tenant has its own copy and editing THAT is what they want. Told
        // apart from "not found" because the two call for different actions.
        var alreadyForked = await db.SmsTemplates
            .AnyAsync(
                t => t.TenantId == tenantId && t.TemplateKey == cmd.TemplateKey
                  && t.Locale == locale && t.IsActive,
                ct);

        if (alreadyForked)
            return Result.Fail<SmsTemplateDto>(SmsTemplateErrors.AlreadyForked);

        var system = await db.SmsTemplates
            .FirstOrDefaultAsync(
                t => t.TenantId == null && t.TemplateKey == cmd.TemplateKey
                  && t.Locale == locale && t.IsActive,
                ct);

        if (system is null)
            return Result.Fail<SmsTemplateDto>(SmsTemplateErrors.NotFound);

        // Fork throws DomainException on a non-system row, which cannot happen here — the query
        // above pins TenantId to null. Left to throw rather than defended against: a seeded row
        // with IsSystem false would be a seeding bug and must be loud.
        var fork = system.Fork(tenantId);

        db.SmsTemplates.Add(fork);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "SMS template {Key}/{Locale} forked for tenant {TenantId}",
            cmd.TemplateKey, locale, tenantId);

        return Result.Ok(ListSmsTemplatesHandler.Map(fork, tenantId));
    }
}

// ── Update ──────────────────────────────────────────────────────────────────

internal sealed record UpdateSmsTemplateCommand(string TemplateKey, string Locale, string Body)
    : IRequest<Result<SmsTemplateDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "SmsTemplate";
    public string? ResourceId => $"{TemplateKey}/{Locale}";
}

internal sealed class UpdateSmsTemplateHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser,
    ILogger<UpdateSmsTemplateHandler> logger)
    : IRequestHandler<UpdateSmsTemplateCommand, Result<SmsTemplateDto>>
{
    public async Task<Result<SmsTemplateDto>> Handle(
        UpdateSmsTemplateCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Body))
            return Result.Fail<SmsTemplateDto>(SmsTemplateErrors.BodyRequired);

        var tenantId = currentUser.TenantId;
        var locale = LanguageCode.Normalize(cmd.Locale);

        var current = await db.SmsTemplates
            .AsTracking()
            .FirstOrDefaultAsync(
                t => t.TenantId == tenantId && t.TemplateKey == cmd.TemplateKey
                  && t.Locale == locale && t.IsActive,
                ct);

        // No tenant row: the caller is trying to edit a platform default in place. Reported as
        // not-found with the fork endpoint as the remedy, rather than silently creating one —
        // which would make "edit" and "fork" the same call and lose the audit distinction between
        // taking a copy and changing it.
        if (current is null)
            return Result.Fail<SmsTemplateDto>(SmsTemplateErrors.NotFound);

        // A NEW VERSION, and the old one retired rather than overwritten: what a customer was
        // sent last month has to stay readable. Same posture as the email templates.
        var next = SmsTemplate.Create(
            tenantId: tenantId,
            templateKey: cmd.TemplateKey,
            locale: locale,
            version: current.Version + 1,
            body: cmd.Body.Trim(),
            isSystem: false);

        current.Deactivate();
        db.SmsTemplates.Add(next);
        await db.SaveChangesAsync(ct);

        var segments = SmsSegments.Count(next.Body);

        if (segments > 1)
        {
            // Logged rather than refused: a tenant whose own wording runs to two segments is
            // making a decision about their own bill, and this module does not get to decide how
            // an institution talks to its customers. The response carries the count so the screen
            // can say it plainly.
            logger.LogInformation(
                "SMS template {Key}/{Locale} v{Version} for tenant {TenantId} is {Segments} "
                + "segments — each message will be billed accordingly",
                cmd.TemplateKey, locale, next.Version, tenantId, segments);
        }

        return Result.Ok(ListSmsTemplatesHandler.Map(next, tenantId));
    }
}
