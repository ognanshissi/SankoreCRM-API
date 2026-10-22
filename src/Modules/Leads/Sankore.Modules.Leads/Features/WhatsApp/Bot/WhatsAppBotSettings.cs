namespace Sankore.Modules.Leads.Features.WhatsApp.Bot;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

// ── DTOs ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>The bot's settings screen (WA-FE-11): the institution's switch, the model, every profile.</summary>
/// <param name="ModelAvailable">The deployment has a model. False = no bot anywhere, whatever is switched on.</param>
public sealed record WhatsAppBotOverviewDto(
    bool TenantEnabled,
    bool ModelAvailable,
    string Provider,
    string? ModelName,
    IReadOnlyList<WhatsAppBotProfileRowDto> Profiles,
    IReadOnlyList<WhatsAppBotFieldDto> AvailableFields);

/// <param name="ProductLineId">Null for the general profile.</param>
/// <param name="Version">0 when never saved.</param>
public sealed record WhatsAppBotProfileRowDto(
    Guid? ProductLineId,
    string Name,
    string? Category,
    bool ProductActive,
    int Version,
    bool IsEnabled,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByName);

/// <param name="IsCustom">A custom field of the tenant's lead form (<c>custom.&lt;key&gt;</c>).</param>
public sealed record WhatsAppBotFieldDto(string Code, string Label, bool IsCustom = false);

public sealed record WhatsAppBotFaqEntryDto(string Question, string Answer);

/// <param name="Day">0 = dimanche … 6 = samedi.</param>
/// <param name="Start">« HH:mm ».</param>
/// <param name="End">« HH:mm », exclusive.</param>
public sealed record WhatsAppBotOpeningSlotDto(int Day, string Start, string End);

public sealed record WhatsAppBotVersionDto(int Version, bool IsEnabled, Guid CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt);

/// <summary>One version of a profile, and — when it is the current one — the history of every save.</summary>
public sealed record WhatsAppBotProfileDto(
    Guid? ProductLineId,
    string Name,
    int Version,
    bool IsEnabled,
    string? WelcomeMessage,
    string? OutOfHoursMessage,
    string? Instructions,
    IReadOnlyList<WhatsAppBotFaqEntryDto> Faq,
    IReadOnlyList<string> FieldsToCollect,
    IReadOnlyList<WhatsAppBotOpeningSlotDto> AgentHours,
    string TimeZoneId,
    IReadOnlyList<string> EscalationKeywords,
    int MaxTurnsWithoutAnswer,
    int WindowExpiryThresholdMinutes,
    IReadOnlyList<string> FieldsRequiredForLead,
    DateTimeOffset? CreatedAt,
    string? CreatedByName,
    IReadOnlyList<WhatsAppBotVersionDto> History,
    IReadOnlyList<WhatsAppBotFieldDto> AvailableFields);

/// <param name="ExpectedVersion">The version the screen was editing — 0 for a first save.</param>
public sealed record SaveWhatsAppBotProfileRequest(
    int ExpectedVersion,
    bool IsEnabled,
    string? WelcomeMessage,
    string? OutOfHoursMessage,
    string? Instructions,
    IReadOnlyList<WhatsAppBotFaqEntryDto>? Faq,
    IReadOnlyList<string>? FieldsToCollect,
    IReadOnlyList<WhatsAppBotOpeningSlotDto>? AgentHours,
    string? TimeZoneId,
    IReadOnlyList<string>? EscalationKeywords,
    int MaxTurnsWithoutAnswer,
    int WindowExpiryThresholdMinutes,
    IReadOnlyList<string>? FieldsRequiredForLead = null);

public sealed record SetWhatsAppBotEnabledRequest(bool Enabled);

internal static class WhatsAppBotErrors
{
    public const string ProductUnknown = "WHATSAPP_BOT_PRODUCT_UNKNOWN";
    public const string VersionNotFound = "WHATSAPP_BOT_VERSION_NOT_FOUND";
    public const string Conflict = "WHATSAPP_BOT_PROFILE_CONFLICT";
    public const string CatalogueUnavailable = "WHATSAPP_BOT_CATALOGUE_UNAVAILABLE";

    public const string GeneralName = "Profil général";
}

// ── Overview ─────────────────────────────────────────────────────────────────────────────────

internal sealed record GetWhatsAppBotOverviewQuery : IRequest<Result<WhatsAppBotOverviewDto>>;

/// <summary>
/// Every product of the catalogue with its profile's current version, plus the general profile
/// first. A product nobody configured reads « version 0, désactivé » — which is exactly how the
/// bot treats it.
/// </summary>
internal sealed class GetWhatsAppBotOverviewHandler(
    LeadsDbContext db,
    ILeadSettings settings,
    IWhatsAppBotModel model,
    IAdministrationModule admin,
    WhatsAppBotFieldValues fieldValues,
    ICurrentUser currentUser)
    : IRequestHandler<GetWhatsAppBotOverviewQuery, Result<WhatsAppBotOverviewDto>>
{
    public async Task<Result<WhatsAppBotOverviewDto>> Handle(GetWhatsAppBotOverviewQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;

        IReadOnlyList<ProductSummary> products;

        try
        {
            products = await admin.ListProductsAsync(tenantId, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<WhatsAppBotOverviewDto>(WhatsAppBotErrors.CatalogueUnavailable);
        }

        // The latest version per line, in one read: a tenant has tens of lines and a few saves each.
        var latest = (await db.WhatsAppBotProfiles
                .Select(p => new { p.ProductLineId, p.Version, p.IsEnabled, p.CreatedAt, p.CreatedBy })
                .ToListAsync(ct))
            .GroupBy(p => p.ProductLineId)
            .ToDictionary(g => g.Key, g => g.MaxBy(p => p.Version)!);

        var authors = latest.Values.Select(v => v.CreatedBy).Where(id => id != Guid.Empty).Distinct().ToList();
        var names = authors.Count == 0 ? new Dictionary<Guid, string>() : await admin.GetUserNamesAsync(tenantId, authors, ct);

        WhatsAppBotProfileRowDto Row(Guid line, string name, string? category, bool active)
        {
            var current = latest.GetValueOrDefault(line);
            return new WhatsAppBotProfileRowDto(
                line == Guid.Empty ? null : line, name, category, active,
                current?.Version ?? 0, current?.IsEnabled ?? false, current?.CreatedAt,
                current is null ? null : names.GetValueOrDefault(current.CreatedBy));
        }

        var rows = new List<WhatsAppBotProfileRowDto> { Row(Guid.Empty, WhatsAppBotErrors.GeneralName, null, true) };
        rows.AddRange(products
            .Where(p => p.Id != Guid.Empty)
            .OrderByDescending(p => p.IsActive).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => Row(p.Id, p.Name, p.Category, p.IsActive)));

        return Result.Ok(new WhatsAppBotOverviewDto(
            await settings.GetBoolAsync(tenantId, LeadSettingKeys.WhatsAppBotEnabled, ct),
            model.IsAvailable,
            model.Provider,
            model.IsAvailable ? model.Name : null,
            rows,
            Fields((await fieldValues.ContextAsync(tenantId, ct)).LeadForm)));
    }

    /// <summary>
    /// The native fields, then the lead form's custom fields the bot may collect — an unreadable
    /// form lists the native ones only.
    /// </summary>
    internal static IReadOnlyList<WhatsAppBotFieldDto> Fields(Sankore.Modules.Administration.PublicApi.Forms.EntityFormDto? leadForm) =>
        WhatsAppBotFields.All.Select(f => new WhatsAppBotFieldDto(f.Code, f.Label))
            .Concat((leadForm?.Fields ?? [])
                .Where(BotFieldContext.IsCollectable)
                .OrderBy(f => f.Order)
                .Select(f => new WhatsAppBotFieldDto(WhatsAppBotFields.CustomPrefix + f.Key, f.Label, IsCustom: true)))
            .ToList();
}

// ── One profile ──────────────────────────────────────────────────────────────────────────────

/// <param name="Version">Null = the current one, with its history.</param>
internal sealed record GetWhatsAppBotProfileQuery(Guid ProductLineId, int? Version) : IRequest<Result<WhatsAppBotProfileDto>>;

internal sealed class GetWhatsAppBotProfileHandler(
    LeadsDbContext db,
    IAdministrationModule admin,
    WhatsAppBotFieldValues fieldValues,
    ICurrentUser currentUser)
    : IRequestHandler<GetWhatsAppBotProfileQuery, Result<WhatsAppBotProfileDto>>
{
    public async Task<Result<WhatsAppBotProfileDto>> Handle(GetWhatsAppBotProfileQuery query, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var name = await WhatsAppBotProfileNames.NameOfAsync(admin, tenantId, query.ProductLineId, ct);
        if (name.IsFailure) return Result.Fail<WhatsAppBotProfileDto>(name.Error!);

        var versions = await db.WhatsAppBotProfiles
            .Where(p => p.ProductLineId == query.ProductLineId)
            .OrderByDescending(p => p.Version)
            .ToListAsync(ct);

        WhatsAppBotProfile profile;

        if (query.Version is { } wanted)
        {
            profile = versions.FirstOrDefault(v => v.Version == wanted)!;
            if (profile is null) return Result.Fail<WhatsAppBotProfileDto>(WhatsAppBotErrors.VersionNotFound);
        }
        else
        {
            profile = versions.FirstOrDefault() ?? WhatsAppBotProfile.Default(tenantId, query.ProductLineId);
        }

        var authors = versions.Select(v => v.CreatedBy).Where(id => id != Guid.Empty).Distinct().ToList();
        var names = authors.Count == 0 ? new Dictionary<Guid, string>() : await admin.GetUserNamesAsync(tenantId, authors, ct);

        var fields = GetWhatsAppBotOverviewHandler.Fields((await fieldValues.ContextAsync(tenantId, ct)).LeadForm);

        return Result.Ok(ToDto(profile, name.Value!, names,
            query.Version is null
                ? versions.Select(v => new WhatsAppBotVersionDto(v.Version, v.IsEnabled, v.CreatedBy, names.GetValueOrDefault(v.CreatedBy), v.CreatedAt)).ToList()
                : [],
            fields));
    }

    internal static WhatsAppBotProfileDto ToDto(
        WhatsAppBotProfile p, string name, IReadOnlyDictionary<Guid, string> names, IReadOnlyList<WhatsAppBotVersionDto> history,
        IReadOnlyList<WhatsAppBotFieldDto> fields) => new(
        p.IsGeneral ? null : p.ProductLineId,
        name,
        p.Version,
        p.IsEnabled,
        p.WelcomeMessage,
        p.OutOfHoursMessage,
        p.Instructions,
        p.Faq.Select(f => new WhatsAppBotFaqEntryDto(f.Question, f.Answer)).ToList(),
        p.FieldsToCollect,
        p.AgentHours.Select(s => new WhatsAppBotOpeningSlotDto((int)s.Day, s.Start, s.End)).ToList(),
        p.TimeZoneId,
        p.EscalationKeywords,
        p.MaxTurnsWithoutAnswer,
        p.WindowExpiryThresholdMinutes,
        p.FieldsRequiredForLead,
        p.Version == 0 ? null : p.CreatedAt,
        p.Version == 0 ? null : names.GetValueOrDefault(p.CreatedBy),
        history,
        fields);
}

// ── Save: a new version ──────────────────────────────────────────────────────────────────────

internal sealed record SaveWhatsAppBotProfileCommand(Guid ProductLineId, SaveWhatsAppBotProfileRequest Request)
    : IRequest<Result<WhatsAppBotProfileDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppBotProfile";
    public string? ResourceId => ProductLineId == Guid.Empty ? "general" : ProductLineId.ToString();
}

/// <summary>
/// Writes version n+1 (WA-BE-12). <c>ExpectedVersion</c> stops two administrators from
/// overwriting each other — the second save is refused instead of silently discarding the first,
/// and the unique (tenant, line, version) index settles a true race the same way.
/// </summary>
internal sealed class SaveWhatsAppBotProfileHandler(
    LeadsDbContext db,
    IAdministrationModule admin,
    WhatsAppBotFieldValues fieldValues,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<SaveWhatsAppBotProfileCommand, Result<WhatsAppBotProfileDto>>
{
    public async Task<Result<WhatsAppBotProfileDto>> Handle(SaveWhatsAppBotProfileCommand cmd, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var r = cmd.Request;

        Result<string> name;

        // M12 reads through its own connection: outside the command's transaction, or it would be
        // promoted to a distributed one Npgsql does not support.
        using (var suppressed = new System.Transactions.TransactionScope(
                   System.Transactions.TransactionScopeOption.Suppress,
                   System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            name = await WhatsAppBotProfileNames.NameOfAsync(admin, tenantId, cmd.ProductLineId, ct);
            suppressed.Complete();
        }

        if (name.IsFailure) return Result.Fail<WhatsAppBotProfileDto>(name.Error!);

        // A custom field must be one the bot can collect NOW: present on the lead form, live, not
        // sensitive. Checked at the save, against what the screen offered; a field archived later is
        // simply no longer offered at a turn.
        var fields = GetWhatsAppBotOverviewHandler.Fields((await fieldValues.ContextAsync(tenantId, ct)).LeadForm);
        var custom = (r.FieldsToCollect ?? []).Select(WhatsAppBotFields.Normalize).Where(WhatsAppBotFields.IsCustom);

        if (custom.Any(code => fields.All(f => f.Code != code)))
            return Result.Fail<WhatsAppBotProfileDto>("WHATSAPP_BOT_FIELD_UNKNOWN");

        var current = await db.WhatsAppBotProfiles
            .Where(p => p.ProductLineId == cmd.ProductLineId)
            .MaxAsync(p => (int?)p.Version, ct) ?? 0;

        if (current != r.ExpectedVersion) return Result.Fail<WhatsAppBotProfileDto>(WhatsAppBotErrors.Conflict);

        if (r.AgentHours?.Any(s => s.Day is < 0 or > 6) == true)
            return Result.Fail<WhatsAppBotProfileDto>("WHATSAPP_BOT_HOURS_INVALID");

        var created = WhatsAppBotProfile.Create(
            tenantId,
            cmd.ProductLineId,
            current + 1,
            r.IsEnabled,
            r.WelcomeMessage,
            r.OutOfHoursMessage,
            r.Instructions,
            r.Faq?.Select(f => new WhatsAppBotFaqEntry(f.Question ?? "", f.Answer ?? "")).ToList(),
            r.FieldsToCollect,
            r.AgentHours?.Select(s => new WhatsAppBotOpeningSlot((DayOfWeek)s.Day, s.Start ?? "", s.End ?? "")).ToList(),
            r.TimeZoneId,
            r.EscalationKeywords,
            r.MaxTurnsWithoutAnswer,
            r.WindowExpiryThresholdMinutes,
            currentUser.Id,
            clock.GetUtcNow(),
            r.FieldsRequiredForLead);

        if (created.IsFailure) return Result.Fail<WhatsAppBotProfileDto>(created.Error!);

        db.WhatsAppBotProfiles.Add(created.Value!);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another save took this version number between the read and the write.
            return Result.Fail<WhatsAppBotProfileDto>(WhatsAppBotErrors.Conflict);
        }

        return Result.Ok(GetWhatsAppBotProfileHandler.ToDto(created.Value!, name.Value!, new Dictionary<Guid, string>(), [], fields));
    }
}

internal sealed record SetWhatsAppBotEnabledCommand(bool Enabled) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WhatsAppBot";
    public string? ResourceId => "tenant";
}

/// <summary>The institution's switch (WA-BE-12, « activable par tenant »): one lead setting, audited.</summary>
internal sealed class SetWhatsAppBotEnabledHandler(ILeadSettings settings, ICurrentUser currentUser)
    : IRequestHandler<SetWhatsAppBotEnabledCommand, Result>
{
    public Task<Result> Handle(SetWhatsAppBotEnabledCommand cmd, CancellationToken ct) =>
        settings.SetAsync(currentUser.TenantId, LeadSettingKeys.WhatsAppBotEnabled, cmd.Enabled ? "true" : "false", currentUser.Id, ct);
}

internal static class WhatsAppBotProfileNames
{
    /// <summary>The general profile's name, or the product's — refusing a line this tenant does not have.</summary>
    public static async Task<Result<string>> NameOfAsync(IAdministrationModule admin, Guid tenantId, Guid productLineId, CancellationToken ct)
    {
        if (productLineId == Guid.Empty) return Result.Ok(WhatsAppBotErrors.GeneralName);

        IReadOnlyList<ProductSummary> products;

        try
        {
            products = await admin.ListProductsAsync(tenantId, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<string>(WhatsAppBotErrors.CatalogueUnavailable);
        }

        var product = products.FirstOrDefault(p => p.Id == productLineId);
        return product is null ? Result.Fail<string>(WhatsAppBotErrors.ProductUnknown) : Result.Ok(product.Name);
    }
}

// ── Endpoints ────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The bot's settings (WA-FE-11), under <c>WhatsApp.Bot.Config</c>. A profile is addressed by its
/// product line id, or by <c>general</c> for the general profile.
/// </summary>
public static class WhatsAppBotEndpoints
{
    public static IEndpointRouteBuilder MapWhatsAppBotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("whatsapp/bot", async (ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new GetWhatsAppBotOverviewQuery(), ct)))
            .WithName("GetWhatsAppBotOverview")
            .WithTags("WhatsApp")
            .WithSummary("The WhatsApp bot: the institution's switch, the model, and every product's profile")
            .RequireAuthorization(Permissions.CanConfigureWhatsAppBot.Code)
            .Produces<WhatsAppBotOverviewDto>()
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi();

        app.MapPut("whatsapp/bot/enabled", async (SetWhatsAppBotEnabledRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SetWhatsAppBotEnabledCommand(req.Enabled), ct)))
            .WithName("SetWhatsAppBotEnabled")
            .WithTags("WhatsApp")
            .WithSummary("Switches the WhatsApp bot on or off for the institution")
            .RequireAuthorization(Permissions.CanConfigureWhatsAppBot.Code)
            .Produces(StatusCodes.Status204NoContent)
            .WithOpenApi();

        app.MapGet("whatsapp/bot/profiles/{key}", async (string key, ISender sender, CancellationToken ct) =>
                ParseKey(key) is { } line
                    ? Map(await sender.Send(new GetWhatsAppBotProfileQuery(line, null), ct))
                    : Problem(WhatsAppBotErrors.ProductUnknown))
            .WithName("GetWhatsAppBotProfile")
            .WithTags("WhatsApp")
            .WithSummary("A product's bot profile (or `general`): the current version and the history of every save")
            .RequireAuthorization(Permissions.CanConfigureWhatsAppBot.Code)
            .Produces<WhatsAppBotProfileDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapGet("whatsapp/bot/profiles/{key}/versions/{version:int}",
                async (string key, int version, ISender sender, CancellationToken ct) =>
                    ParseKey(key) is { } line
                        ? Map(await sender.Send(new GetWhatsAppBotProfileQuery(line, version), ct))
                        : Problem(WhatsAppBotErrors.ProductUnknown))
            .WithName("GetWhatsAppBotProfileVersion")
            .WithTags("WhatsApp")
            .WithSummary("One past version of a bot profile, as it was saved")
            .RequireAuthorization(Permissions.CanConfigureWhatsAppBot.Code)
            .Produces<WhatsAppBotProfileDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        app.MapPut("whatsapp/bot/profiles/{key}",
                async (string key, SaveWhatsAppBotProfileRequest req, ISender sender, CancellationToken ct) =>
                    ParseKey(key) is { } line
                        ? Map(await sender.Send(new SaveWhatsAppBotProfileCommand(line, req), ct))
                        : Problem(WhatsAppBotErrors.ProductUnknown))
            .WithName("SaveWhatsAppBotProfile")
            .WithTags("WhatsApp")
            .WithSummary("Saves a bot profile as a new version; refused with 409 when it changed since it was read")
            .RequireAuthorization(Permissions.CanConfigureWhatsAppBot.Code)
            .Produces<WhatsAppBotProfileDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static Guid? ParseKey(string key) =>
        string.Equals(key, "general", StringComparison.OrdinalIgnoreCase) ? Guid.Empty
        : Guid.TryParse(key, out var id) && id != Guid.Empty ? id
        : null;

    private static IResult Map<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    private static IResult Map(Result result) => result.IsSuccess ? Results.NoContent() : Problem(result.Error!);

    private static IResult Problem(string code) => code switch
    {
        WhatsAppBotErrors.ProductUnknown or WhatsAppBotErrors.VersionNotFound
            => Results.Problem(title: "WhatsApp bot", detail: code, statusCode: StatusCodes.Status404NotFound),
        WhatsAppBotErrors.Conflict => Results.Problem(title: "WhatsApp bot", detail: code, statusCode: StatusCodes.Status409Conflict),
        WhatsAppBotErrors.CatalogueUnavailable => Results.Problem(title: "WhatsApp bot", detail: code, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Problem(title: "WhatsApp bot", detail: code, statusCode: StatusCodes.Status422UnprocessableEntity),
    };
}
