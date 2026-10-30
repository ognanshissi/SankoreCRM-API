namespace Sankore.Modules.Integration.Features.Webhooks;

using FluentValidation;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Egress;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Kernel;

// ── Contract ─────────────────────────────────────────────────────────────────────────────────

/// <param name="SecretHint">The secret, masked (<c>whse****wxyz</c>): enough to tell which one a partner holds.</param>
/// <param name="PreviousSecretValidUntil">After a rotation, until when the previous secret still signs.</param>
public sealed record WebhookSubscriptionDto(
    Guid Id,
    string Name,
    string Url,
    string? Description,
    IReadOnlyList<string> Events,
    bool IsActive,
    int ConsecutiveFailures,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailureCode,
    DateTimeOffset? DisabledAt,
    string? SecretHint,
    DateTimeOffset? PreviousSecretValidUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="Secret">Shown ONCE. No endpoint returns it again; a lost secret is rotated.</param>
public sealed record CreatedWebhookSubscriptionDto(
    WebhookSubscriptionDto Subscription,
    [property: SensitiveData] string Secret);

/// <param name="Secret">Shown ONCE.</param>
public sealed record RotatedWebhookSecretDto(
    [property: SensitiveData] string Secret,
    DateTimeOffset PreviousValidUntil);

/// <param name="Available">False when the event needs a module the tenant does not have.</param>
/// <param name="SamplePayload">The body a delivery of this event looks like, as JSON.</param>
public sealed record WebhookEventTypeDto(
    string Code,
    int Version,
    string Family,
    string Label,
    string Description,
    bool Available,
    string SamplePayload);

public sealed record SaveWebhookSubscriptionRequest(string Name, string Url, string? Description, IReadOnlyList<string> Events);

public sealed record SetWebhookSubscriptionActiveRequest(bool IsActive);

/// <param name="EventCode">The event to simulate; the subscription's first one when absent.</param>
public sealed record TestWebhookSubscriptionRequest(string? EventCode);

/// <param name="FailureCode">Null on success; <c>HTTP_&lt;status&gt;</c>, <c>TIMEOUT</c>, <c>UNREACHABLE</c>,
/// <c>ADDRESS_REFUSED</c> or <c>REDIRECT_REFUSED</c> otherwise.</param>
public sealed record WebhookTestResultDto(string EventCode, bool Succeeded, int? StatusCode, int DurationMs, string? FailureCode);

// ── Reads ────────────────────────────────────────────────────────────────────────────────────

internal sealed record ListWebhookSubscriptionsQuery : IRequest<IReadOnlyList<WebhookSubscriptionDto>>;

internal sealed class ListWebhookSubscriptionsHandler(IntegrationDbContext db, WebhookSubscriptionMapper mapper)
    : IRequestHandler<ListWebhookSubscriptionsQuery, IReadOnlyList<WebhookSubscriptionDto>>
{
    public async Task<IReadOnlyList<WebhookSubscriptionDto>> Handle(ListWebhookSubscriptionsQuery query, CancellationToken ct)
    {
        var subscriptions = await db.WebhookSubscriptions.OrderBy(s => s.Name).ToListAsync(ct);
        var result = new List<WebhookSubscriptionDto>(subscriptions.Count);
        foreach (var s in subscriptions) result.Add(await mapper.ToDtoAsync(s, ct));
        return result;
    }
}

internal sealed record GetWebhookSubscriptionQuery(Guid Id) : IRequest<Result<WebhookSubscriptionDto>>;

internal sealed class GetWebhookSubscriptionHandler(IntegrationDbContext db, WebhookSubscriptionMapper mapper)
    : IRequestHandler<GetWebhookSubscriptionQuery, Result<WebhookSubscriptionDto>>
{
    public async Task<Result<WebhookSubscriptionDto>> Handle(GetWebhookSubscriptionQuery query, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == query.Id, ct);
        return subscription is null
            ? Result.Fail<WebhookSubscriptionDto>(WebhookErrors.NotFound)
            : Result.Ok(await mapper.ToDtoAsync(subscription, ct));
    }
}

internal sealed record ListWebhookEventTypesQuery : IRequest<IReadOnlyList<WebhookEventTypeDto>>;

internal sealed class ListWebhookEventTypesHandler(ICurrentUser me, ITenantModules modules)
    : IRequestHandler<ListWebhookEventTypesQuery, IReadOnlyList<WebhookEventTypeDto>>
{
    public async Task<IReadOnlyList<WebhookEventTypeDto>> Handle(ListWebhookEventTypesQuery query, CancellationToken ct)
    {
        var result = new List<WebhookEventTypeDto>();

        // One entry per CODE: client.updated comes from two events that read the same.
        foreach (var definition in WebhookEventCatalog.All.DistinctBy(d => d.Code))
        {
            var available = definition.RequiredModule is null
                            || await modules.IsEnabledAsync(me.TenantId, definition.RequiredModule, ct);

            result.Add(new WebhookEventTypeDto(
                definition.Code, definition.Version, definition.Family, definition.Label, definition.Description,
                available, WebhookPayload.Body(definition, definition.Sample(me.TenantId))));
        }

        return result;
    }
}

// ── Writes ───────────────────────────────────────────────────────────────────────────────────

internal sealed record SaveWebhookSubscriptionCommand(Guid? Id, string Name, string Url, string? Description, IReadOnlyList<string> Events)
    : IRequest<Result<CreatedWebhookSubscriptionDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "WebhookSubscription";
    public string? ResourceId => Id?.ToString();
}

/// <summary>
/// What can be told without the database: the URL against the platform's egress policy (HTTPS,
/// allowed ports, no internal address), and every code against the catalogue — so the
/// administrator reads the reason on the field instead of a code.
/// </summary>
internal sealed class SaveWebhookSubscriptionValidator : AbstractValidator<SaveWebhookSubscriptionCommand>
{
    public SaveWebhookSubscriptionValidator(IOptions<OutboundHttpOptions> egress)
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Le nom est obligatoire.")
            .MaximumLength(WebhookSubscription.NameMaxLength);

        RuleFor(x => x.Url).Custom((url, ctx) =>
        {
            var problem = SafeOutboundHttp.ValidateUrl(url, egress.Value);
            if (problem is not null) ctx.AddFailure(nameof(SaveWebhookSubscriptionCommand.Url), problem);
        });

        RuleFor(x => x.Description).MaximumLength(WebhookSubscription.DescriptionMaxLength);

        RuleFor(x => x.Events)
            .NotEmpty().WithMessage("Choisissez au moins un événement.")
            .Must(e => e is null || e.Count <= WebhookSubscription.MaxEvents)
            .WithMessage($"{WebhookSubscription.MaxEvents} événements au plus.");

        RuleForEach(x => x.Events)
            .Must(WebhookEventCatalog.IsKnownCode)
            .WithMessage((_, code) => $"Événement inconnu : « {code} ».");
    }
}

internal sealed class SaveWebhookSubscriptionHandler(
    IntegrationDbContext db,
    ICurrentUser me,
    ITenantModules modules,
    WebhookSecrets secrets,
    WebhookSubscriptionMapper mapper,
    TimeProvider clock)
    : IRequestHandler<SaveWebhookSubscriptionCommand, Result<CreatedWebhookSubscriptionDto>>
{
    /// <summary>
    /// Every event is fanned out to every active subscription of the tenant (lot 3): the ceiling
    /// bounds what one event costs, and twenty partners is well past what an institution wires.
    /// </summary>
    internal const int MaxPerTenant = 20;

    /// <returns>On creation, the subscription and its secret; on update, the subscription and an empty secret.</returns>
    public async Task<Result<CreatedWebhookSubscriptionDto>> Handle(SaveWebhookSubscriptionCommand cmd, CancellationToken ct)
    {
        foreach (var code in cmd.Events.Distinct())
        {
            var module = WebhookEventCatalog.All.First(d => d.Code == code).RequiredModule;
            if (module is not null && !await modules.IsEnabledAsync(me.TenantId, module, ct))
                return Result.Fail<CreatedWebhookSubscriptionDto>(WebhookErrors.EventModuleDisabled);
        }

        var now = clock.GetUtcNow();

        if (cmd.Id is { } id)
        {
            var existing = await db.WebhookSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (existing is null) return Result.Fail<CreatedWebhookSubscriptionDto>(WebhookErrors.NotFound);

            var updated = existing.Update(cmd.Name, cmd.Url, cmd.Description, cmd.Events, me.Id, now);
            if (updated.IsFailure) return Result.Fail<CreatedWebhookSubscriptionDto>(updated.Error!);

            await db.SaveChangesAsync(ct);
            return Result.Ok(new CreatedWebhookSubscriptionDto(await mapper.ToDtoAsync(existing, ct), string.Empty));
        }

        if (await db.WebhookSubscriptions.CountAsync(ct) >= MaxPerTenant)
            return Result.Fail<CreatedWebhookSubscriptionDto>(WebhookErrors.TooMany);

        var created = WebhookSubscription.Create(me.TenantId, cmd.Name, cmd.Url, cmd.Description, cmd.Events, me.Id, now);
        if (created.IsFailure) return Result.Fail<CreatedWebhookSubscriptionDto>(created.Error!);

        db.WebhookSubscriptions.Add(created.Value);
        await db.SaveChangesAsync(ct);

        // In the command's transaction: a subscription without a secret would sign nothing.
        var secret = await secrets.CreateAsync(me.TenantId, created.Value.Id, ct);
        return Result.Ok(new CreatedWebhookSubscriptionDto(await mapper.ToDtoAsync(created.Value, ct), secret));
    }
}

internal sealed record SetWebhookSubscriptionActiveCommand(Guid Id, bool IsActive)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WebhookSubscription";
    public string? ResourceId => Id.ToString();
}

internal sealed class SetWebhookSubscriptionActiveHandler(IntegrationDbContext db, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<SetWebhookSubscriptionActiveCommand, Result>
{
    public async Task<Result> Handle(SetWebhookSubscriptionActiveCommand cmd, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == cmd.Id, ct);
        if (subscription is null) return Result.Fail(WebhookErrors.NotFound);

        subscription.SetActive(cmd.IsActive, me.Id, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal sealed record RotateWebhookSecretCommand(Guid Id)
    : IRequest<Result<RotatedWebhookSecretDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "WebhookSubscriptionSecret";
    public string? ResourceId => Id.ToString();
}

internal sealed class RotateWebhookSecretHandler(IntegrationDbContext db, ICurrentUser me, WebhookSecrets secrets)
    : IRequestHandler<RotateWebhookSecretCommand, Result<RotatedWebhookSecretDto>>
{
    public async Task<Result<RotatedWebhookSecretDto>> Handle(RotateWebhookSecretCommand cmd, CancellationToken ct)
    {
        if (!await db.WebhookSubscriptions.AnyAsync(s => s.Id == cmd.Id, ct))
            return Result.Fail<RotatedWebhookSecretDto>(WebhookErrors.NotFound);

        var (secret, until) = await secrets.RotateAsync(me.TenantId, cmd.Id, ct);
        return Result.Ok(new RotatedWebhookSecretDto(secret, until));
    }
}

internal sealed record DeleteWebhookSubscriptionCommand(Guid Id)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WebhookSubscription";
    public string? ResourceId => Id.ToString();
}

internal sealed class DeleteWebhookSubscriptionHandler(IntegrationDbContext db, ICurrentUser me, WebhookSecrets secrets)
    : IRequestHandler<DeleteWebhookSubscriptionCommand, Result>
{
    public async Task<Result> Handle(DeleteWebhookSubscriptionCommand cmd, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == cmd.Id, ct);
        if (subscription is null) return Result.Fail(WebhookErrors.NotFound);

        db.WebhookSubscriptions.Remove(subscription);
        await db.SaveChangesAsync(ct);
        await secrets.DeleteAsync(me.TenantId, cmd.Id, ct);
        return Result.Ok();
    }
}

/// <summary>
/// « Envoyer un test »: the sample of one event, marked <c>"test": true</c>, signed and sent now.
/// Nothing is recorded on the subscription — a test says whether the partner answers, not how the
/// subscription is doing.
/// </summary>
internal sealed record TestWebhookSubscriptionCommand(Guid Id, string? EventCode)
    : IRequest<Result<WebhookTestResultDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "WebhookSubscription";
    public string? ResourceId => Id.ToString();
}

internal sealed class TestWebhookSubscriptionHandler(
    IntegrationDbContext db, ICurrentUser me, WebhookSecrets secrets, WebhookSender sender)
    : IRequestHandler<TestWebhookSubscriptionCommand, Result<WebhookTestResultDto>>
{
    public async Task<Result<WebhookTestResultDto>> Handle(TestWebhookSubscriptionCommand cmd, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == cmd.Id, ct);
        if (subscription is null) return Result.Fail<WebhookTestResultDto>(WebhookErrors.NotFound);

        var code = string.IsNullOrWhiteSpace(cmd.EventCode) ? subscription.Events.FirstOrDefault() : cmd.EventCode.Trim();
        var definition = WebhookEventCatalog.All.FirstOrDefault(d => d.Code == code);
        if (definition is null) return Result.Fail<WebhookTestResultDto>(WebhookErrors.EventUnknown);

        var sample = definition.Sample(me.TenantId);
        var body = WebhookPayload.Body(definition, sample, test: true);
        var keys = await secrets.SigningKeysAsync(me.TenantId, subscription.Id, ct);

        var attempt = await sender.SendAsync(subscription.Url, definition.Code, sample.EventId, body, keys, ct);
        return Result.Ok(new WebhookTestResultDto(definition.Code, attempt.Succeeded, attempt.StatusCode, attempt.DurationMs, attempt.FailureCode));
    }
}

// ── Delivery log ─────────────────────────────────────────────────────────────────────────────

/// <param name="Status"><c>Pending</c>, <c>Succeeded</c>, <c>Failed</c> (attempts exhausted) or <c>Cancelled</c>.</param>
public sealed record WebhookDeliveryDto(
    Guid Id,
    Guid EventId,
    string EventCode,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    int? LastStatusCode,
    string? LastFailureCode,
    int? LastDurationMs,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset CreatedAt);

/// <param name="Body">The exact JSON that was signed and sent; empty once the person it was about was erased.</param>
public sealed record WebhookDeliveryDetailDto(WebhookDeliveryDto Delivery, string Body);

internal sealed record ListWebhookDeliveriesQuery(Guid SubscriptionId, string? Status, int Page, int PageSize)
    : IRequest<Result<PagedResult<WebhookDeliveryDto>>>;

internal sealed class ListWebhookDeliveriesHandler(IntegrationDbContext db)
    : IRequestHandler<ListWebhookDeliveriesQuery, Result<PagedResult<WebhookDeliveryDto>>>
{
    public async Task<Result<PagedResult<WebhookDeliveryDto>>> Handle(ListWebhookDeliveriesQuery query, CancellationToken ct)
    {
        if (!await db.WebhookSubscriptions.AnyAsync(s => s.Id == query.SubscriptionId, ct))
            return Result.Fail<PagedResult<WebhookDeliveryDto>>(WebhookErrors.NotFound);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);

        var rows = db.WebhookDeliveries.Where(d => d.SubscriptionId == query.SubscriptionId);
        if (Enum.TryParse<WebhookDeliveryStatus>(query.Status, ignoreCase: true, out var status))
            rows = rows.Where(d => d.Status == status);

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(d => d.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);

        return Result.Ok(new PagedResult<WebhookDeliveryDto>(items.Select(ToDto).ToList(), total, page, size));
    }

    internal static WebhookDeliveryDto ToDto(WebhookDelivery d) => new(
        d.Id, d.EventId, d.EventCode, d.Status.ToString(), d.Attempts, WebhookDelivery.MaxAttempts, d.NextAttemptAt,
        d.LastAttemptAt, d.LastStatusCode, d.LastFailureCode, d.LastDurationMs, d.DeliveredAt, d.CreatedAt);
}

internal sealed record GetWebhookDeliveryQuery(Guid SubscriptionId, Guid DeliveryId) : IRequest<Result<WebhookDeliveryDetailDto>>;

internal sealed class GetWebhookDeliveryHandler(IntegrationDbContext db)
    : IRequestHandler<GetWebhookDeliveryQuery, Result<WebhookDeliveryDetailDto>>
{
    public async Task<Result<WebhookDeliveryDetailDto>> Handle(GetWebhookDeliveryQuery query, CancellationToken ct)
    {
        var delivery = await db.WebhookDeliveries
            .FirstOrDefaultAsync(d => d.Id == query.DeliveryId && d.SubscriptionId == query.SubscriptionId, ct);

        return delivery is null
            ? Result.Fail<WebhookDeliveryDetailDto>(WebhookErrors.DeliveryNotFound)
            : Result.Ok(new WebhookDeliveryDetailDto(ListWebhookDeliveriesHandler.ToDto(delivery), delivery.Body));
    }
}

/// <summary>
/// « Renvoyer »: the same event id — the partner deduplicates on it — and a fresh round of
/// attempts from now. Refused on an erased delivery, and on one whose subscription is off: it
/// would only be cancelled at its first attempt.
/// </summary>
internal sealed record ReplayWebhookDeliveryCommand(Guid SubscriptionId, Guid DeliveryId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "WebhookDelivery";
    public string? ResourceId => DeliveryId.ToString();
}

internal sealed class ReplayWebhookDeliveryHandler(IntegrationDbContext db, ICurrentUser me, IBackgroundJobClient jobs, TimeProvider clock)
    : IRequestHandler<ReplayWebhookDeliveryCommand, Result>
{
    public async Task<Result> Handle(ReplayWebhookDeliveryCommand cmd, CancellationToken ct)
    {
        var subscription = await db.WebhookSubscriptions.FirstOrDefaultAsync(s => s.Id == cmd.SubscriptionId, ct);
        if (subscription is null) return Result.Fail(WebhookErrors.NotFound);
        if (!subscription.IsActive) return Result.Fail(WebhookErrors.SubscriptionInactive);

        var delivery = await db.WebhookDeliveries.AsTracking()
            .FirstOrDefaultAsync(d => d.Id == cmd.DeliveryId && d.SubscriptionId == cmd.SubscriptionId, ct);
        if (delivery is null) return Result.Fail(WebhookErrors.DeliveryNotFound);

        var replayed = delivery.Replay(clock.GetUtcNow());
        if (replayed.IsFailure) return replayed;

        await db.SaveChangesAsync(ct);

        var tenantId = me.TenantId;
        var id = delivery.Id;
        jobs.Enqueue<WebhookDeliveryJob>(j => j.ExecuteAsync(tenantId, id));
        return Result.Ok();
    }
}

internal sealed class WebhookSubscriptionMapper(ISecretsModule vault, TimeProvider clock)
{
    public async Task<WebhookSubscriptionDto> ToDtoAsync(WebhookSubscription s, CancellationToken ct)
    {
        var hint = await vault.GetHintAsync(new SecretKey(s.TenantId, WebhookSecrets.Scope, s.Id, WebhookSecrets.CurrentName), ct);
        var previous = await vault.GetHintAsync(new SecretKey(s.TenantId, WebhookSecrets.Scope, s.Id, WebhookSecrets.PreviousName), ct);
        var previousUntil = previous?.ExpiresAt is { } until && until > clock.GetUtcNow() ? until : (DateTimeOffset?)null;

        return new WebhookSubscriptionDto(
            s.Id, s.Name, s.Url, s.Description, s.Events, s.IsActive, s.ConsecutiveFailures,
            s.LastSuccessAt, s.LastFailureAt, s.LastFailureCode, s.DisabledAt,
            hint?.MaskedValue, previousUntil, s.CreatedAt, s.UpdatedAt);
    }
}

// ── Endpoints and registration ───────────────────────────────────────────────────────────────

internal static class WebhookSubscriptionsFeature
{
    internal static IServiceCollection AddWebhookSubscriptions(this IServiceCollection services)
    {
        services.AddScoped<WebhookSecrets>();
        services.AddScoped<WebhookSubscriptionMapper>();
        services.AddScoped<WebhookSender>();
        services.AddScoped<WebhookDispatcher>();
        services.AddScoped<ITechnicalPurge, WebhookDeliveryPurge>();

        // No retry here: the delivery queue (lot 3) owns when and how often to try again, and a
        // retry inside the client would hold a partner's slow endpoint for three timeouts per event.
        services.AddSafeOutboundHttpClient(WebhookSender.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = WebhookSender.Timeout);

        return services;
    }

    /// <summary>
    /// <c>integration/webhook-subscriptions</c>, and NOT <c>integration/webhooks</c>: that prefix,
    /// outside <c>api/v1</c>, is the inbound sync hooks', exempt from tenant resolution.
    /// </summary>
    internal static IEndpointRouteBuilder MapWebhookSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/webhook-subscriptions").WithTags("Webhooks");

        // « Copier en production » from the test mode (sandbox lot 4)
        group.MapPromoteWebhookSubscription();

        group.MapGet("", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListWebhookSubscriptionsQuery(), ct)))
            .WithName("ListWebhookSubscriptions")
            .WithSummary("The institution's outbound webhooks, with their delivery health and a masked secret")
            .RequireAuthorization(Permissions.CanViewWebhooks.Code)
            .Produces<IReadOnlyList<WebhookSubscriptionDto>>()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("event-types", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListWebhookEventTypesQuery(), ct)))
            .WithName("ListWebhookEventTypes")
            .WithSummary("The events a webhook may subscribe to, each with a sample payload")
            .RequireAuthorization(Permissions.CanViewWebhooks.Code)
            .Produces<IReadOnlyList<WebhookEventTypeDto>>()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new GetWebhookSubscriptionQuery(id), ct)))
            .WithName("GetWebhookSubscription")
            .RequireAuthorization(Permissions.CanViewWebhooks.Code)
            .Produces<WebhookSubscriptionDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("", async (SaveWebhookSubscriptionRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SaveWebhookSubscriptionCommand(null, req.Name, req.Url, req.Description, req.Events ?? []), ct)))
            .WithName("CreateWebhookSubscription")
            .WithSummary("Creates a webhook; the answer carries its signing secret, shown this once")
            .RequireAuthorization(Permissions.CanManageWebhooks.Code)
            .Produces<CreatedWebhookSubscriptionDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("{id:guid}", async (Guid id, SaveWebhookSubscriptionRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SaveWebhookSubscriptionCommand(id, req.Name, req.Url, req.Description, req.Events ?? []), ct), r => r.Subscription))
            .WithName("UpdateWebhookSubscription")
            .WithSummary("Changes the name, address, description or events; the secret is unchanged")
            .RequireAuthorization(Permissions.CanManageWebhooks.Code)
            .Produces<WebhookSubscriptionDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("{id:guid}/active", async (Guid id, SetWebhookSubscriptionActiveRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SetWebhookSubscriptionActiveCommand(id, req.IsActive), ct)))
            .WithName("SetWebhookSubscriptionActive")
            .WithSummary("Switches the webhook on or off; switching on clears its failure streak")
            .RequireAuthorization(Permissions.CanManageWebhooks.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/secret/rotate", async (Guid id, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new RotateWebhookSecretCommand(id), ct)))
            .WithName("RotateWebhookSecret")
            .WithSummary("Issues a new signing secret, shown this once; the previous one keeps signing for 7 days")
            .RequireAuthorization(Permissions.CanManageWebhooks.Code)
            .Produces<RotatedWebhookSecretDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/test", async (Guid id, TestWebhookSubscriptionRequest? req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new TestWebhookSubscriptionCommand(id, req?.EventCode), ct)))
            .WithName("TestWebhookSubscription")
            .WithSummary("Sends a signed sample of one event, marked test, and reports how the partner answered")
            .RequireAuthorization(Permissions.CanManageWebhooks.Code)
            .Produces<WebhookTestResultDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{id:guid}/deliveries", async (Guid id, string? status, int? page, int? pageSize, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new ListWebhookDeliveriesQuery(id, status, page ?? 1, pageSize ?? 20), ct)))
            .WithName("ListWebhookDeliveries")
            .WithSummary("The delivery log of a webhook, newest first; `status` = Pending | Succeeded | Failed | Cancelled")
            .RequireAuthorization(Permissions.CanViewWebhooks.Code)
            .Produces<PagedResult<WebhookDeliveryDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{id:guid}/deliveries/{deliveryId:guid}", async (Guid id, Guid deliveryId, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new GetWebhookDeliveryQuery(id, deliveryId), ct)))
            .WithName("GetWebhookDelivery")
            .WithSummary("One delivery, with the exact body that was signed and sent")
            .RequireAuthorization(Permissions.CanViewWebhooks.Code)
            .Produces<WebhookDeliveryDetailDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/deliveries/{deliveryId:guid}/replay", async (Guid id, Guid deliveryId, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new ReplayWebhookDeliveryCommand(id, deliveryId), ct)))
            .WithName("ReplayWebhookDelivery")
            .WithSummary("Sends a delivery again — same event id, a fresh round of attempts")
            .RequireAuthorization(Permissions.CanManageWebhooks.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapDelete("{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new DeleteWebhookSubscriptionCommand(id), ct)))
            .WithName("DeleteWebhookSubscription")
            .WithSummary("Deletes the webhook and its secrets")
            .RequireAuthorization(Permissions.CanManageWebhooks.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static IResult Map<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    private static IResult Map<T, TOut>(Result<T> result, Func<T, TOut> project)
        => result.IsSuccess ? Results.Ok(project(result.Value!)) : Problem(result.Error!);

    private static IResult Map(Result result) => result.IsSuccess ? Results.NoContent() : Problem(result.Error!);

    private static IResult Problem(string code) => code switch
    {
        WebhookErrors.NotFound or WebhookErrors.DeliveryNotFound
            => Results.Problem(title: "Webhooks", detail: code, statusCode: StatusCodes.Status404NotFound),
        _ => Results.Problem(title: "Webhooks", detail: code, statusCode: StatusCodes.Status422UnprocessableEntity),
    };
}
