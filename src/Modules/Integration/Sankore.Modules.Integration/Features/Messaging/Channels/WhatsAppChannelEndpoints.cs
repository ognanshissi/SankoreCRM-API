namespace Sankore.Modules.Integration.Features.Messaging.Channels;

using System.Security.Cryptography;
using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Connections;
using Sankore.Modules.Integration.Features.Connections.ActivateConnection;
using Sankore.Modules.Integration.Features.Connections.CheckConnectionHealth;
using Sankore.Modules.Integration.Features.Connections.CreateConnection;
using Sankore.Modules.Integration.Features.Connections.DeactivateConnection;
using Sankore.Modules.Integration.Features.Connections.SetConnectionSecret;
using Sankore.Modules.Integration.Features.Connections.UpdateConnection;
using Sankore.Modules.Integration.Features.Messaging.Webhook;
using Sankore.Modules.Integration.Features.Telephony;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The WhatsApp settings screen's API (WA-BE-02, WA-FE-01): the institution's numbers, their API
/// key, the connection test, the activation and the webhook.
///
/// <para>
/// <b>A view over the connections of the Messaging family, not a second store.</b> Every write is
/// the existing connection command — create, update, secret, health, activate — sent through
/// MediatR, so the validation, the audit row, the exclusivity indexes and the health rule are the
/// ones every other family goes through. What this adds is a permission of its own
/// (<c>WhatsApp.Config</c>, so a marketing administrator can run the number without holding the
/// core banking credentials) and a guard that every id it is handed is a MESSAGING connection of
/// the caller's tenant: without it, this permission would open the other families' rows.
/// </para>
/// </summary>
internal static class WhatsAppChannelEndpoints
{
    internal const string WebhookPublicBaseUrlKey = "Integration:Messaging:PublicBaseUrl";

    internal static IEndpointRouteBuilder MapWhatsAppChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/messaging/channels")
            .WithTags("WhatsApp")
            .RequireAuthorization(Permissions.CanConfigureWhatsApp.Code);

        group.MapGet("", ListAsync)
            .WithName("ListWhatsAppChannels")
            .WithSummary("The institution's WhatsApp numbers, with their status and credential hints")
            .Produces<IReadOnlyList<WhatsAppChannelDto>>()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("", CreateAsync)
            .WithName("CreateWhatsAppChannel")
            .WithSummary("Registers a WhatsApp number (inactive until tested and activated)")
            .WithDescription(
                "Creates the number's connection in the Messaging family. It starts inactive: save "
                + "its 360dialog API key, test it, then activate it. `simulated: true` creates an "
                + "in-memory number instead, in Development only. A phone-number id already active "
                + "elsewhere answers 409 MESSAGING_NUMBER_ALREADY_CONNECTED at activation. "
                + "Requires permission: WhatsApp.Config.")
            .Produces<Guid>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("{id:guid}", UpdateAsync)
            .WithName("UpdateWhatsAppChannel")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("{id:guid}/api-key", SetApiKeyAsync)
            .WithName("SetWhatsAppChannelApiKey")
            .WithSummary("Stores the number's 360dialog API key in the vault. Never returned.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/check", CheckAsync)
            .WithName("CheckWhatsAppChannel")
            .WithSummary("Tests the connection with 360dialog and records the verdict")
            .Produces<ConnectionHealthDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/activate", ActivateAsync)
            .WithName("ActivateWhatsAppChannel")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/deactivate", DeactivateAsync)
            .WithName("DeactivateWhatsAppChannel")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/webhook", ConfigureWebhookAsync)
            .WithName("ConfigureWhatsAppWebhook")
            .WithSummary("Generates the webhook token and points 360dialog's webhook at this platform")
            .WithDescription(
                "Generates a fresh webhook token, stores it in the vault, and asks 360dialog to "
                + "deliver to this platform's WhatsApp webhook with the token as the "
                + "X-Sankore-Webhook-Token header. Running it again rotates the token. The URL is "
                + "built from Integration:Messaging:PublicBaseUrl when configured, otherwise from "
                + "the address this request was made to. Requires permission: WhatsApp.Config.")
            .Produces<ConfigureWhatsAppWebhookResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/simulate/inbound", SimulateInboundAsync)
            .WithName("SimulateWhatsAppInbound")
            .WithSummary("DEVELOPMENT ONLY — a customer writes to this number")
            .WithDescription(
                "Builds a Cloud API delivery for one text message and runs the REAL webhook path: "
                + "token verification, storage, the processing job, the attachment to a lead. "
                + "Answers 404 outside Development.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/simulate/status", SimulateStatusAsync)
            .WithName("SimulateWhatsAppStatus")
            .WithSummary("DEVELOPMENT ONLY — the provider reports a delivery status")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    // ── Reads ────────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListAsync(
        MessagingChannels channels, ISecretsModule secrets, ITenantContext tenant, CancellationToken ct)
    {
        var tenantId = tenant.CurrentTenantId;
        var rows = await channels.ListAsync(tenantId, activeOnly: false, ct);
        var result = new List<WhatsAppChannelDto>(rows.Count);

        foreach (var row in rows)
        {
            var apiKey = await secrets.GetHintAsync(IntegrationSecrets.CredentialKey(tenantId, row.Id), ct);
            var webhook = await secrets.GetHintAsync(IntegrationSecrets.WebhookSecretKey(tenantId, row.Id), ct);

            result.Add(WhatsAppChannelDto.From(row, apiKey, webhook is not null));
        }

        return Results.Ok(result);
    }

    // ── Writes, through the connection commands ──────────────────────────────────────────────

    private static async Task<IResult> CreateAsync(
        SaveWhatsAppChannelRequest request, ISender sender, IHostEnvironment environment, CancellationToken ct)
    {
        if (request.Simulated && !environment.IsDevelopment())
            return Results.UnprocessableEntity(new { error = IntegrationErrors.FakeAdapterNotAllowed });

        var (kind, settings) = SettingsOf(request);

        var result = await sender.Send(new CreateConnectionCommand(
            IntegrationFamily.Messaging, kind, IntegrationMode.Api, request.Name, settings), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/integration/messaging/channels/{result.Value}", result.Value)
            : Results.UnprocessableEntity(new { error = result.Error });
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        SaveWhatsAppChannelRequest request,
        ISender sender,
        MessagingChannels channels,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var existing = await channels.GetAsync(tenant.CurrentTenantId, id, ct);
        if (existing is null) return NotFound();

        // The kind is the row's own: a simulated number stays simulated, a real one real.
        var (_, settings) = SettingsOf(request with { Simulated = existing.Kind == IntegrationKind.Fake });

        var result = await sender.Send(
            new UpdateConnectionCommand(id, request.Name, IntegrationMode.Api, settings, request.ExpectedVersion), ct);

        if (result.IsSuccess)
        {
            // The old number's directory entry, if the number moved. Self-healing anyway — see
            // MessagingChannels — but there is no reason to leave it.
            await channels.ForgetAsync(existing.ChannelAddress, ct);
            return Results.NoContent();
        }

        return result.Error switch
        {
            IntegrationErrors.ConnectionNotFound => NotFound(),
            IntegrationErrors.ConcurrencyConflict or IntegrationErrors.MessagingNumberAlreadyConnected
                => Results.Conflict(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }

    private static async Task<IResult> SetApiKeyAsync(
        Guid id,
        SetWhatsAppApiKeyRequest request,
        ISender sender,
        MessagingChannels channels,
        ITenantContext tenant,
        CancellationToken ct)
    {
        if (await channels.GetAsync(tenant.CurrentTenantId, id, ct) is null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.ApiKey) || request.ApiKey.Trim().Length > 500)
            return Results.UnprocessableEntity(new { error = IntegrationErrors.CredentialMissing });

        var result = await sender.Send(
            new SetConnectionSecretCommand(id, IntegrationSecrets.CredentialName, request.ApiKey.Trim()), ct);

        return result.IsSuccess ? Results.NoContent() : Results.UnprocessableEntity(new { error = result.Error });
    }

    private static async Task<IResult> CheckAsync(
        Guid id, ISender sender, MessagingChannels channels, ITenantContext tenant, CancellationToken ct)
    {
        if (await channels.GetAsync(tenant.CurrentTenantId, id, ct) is null) return NotFound();

        var result = await sender.Send(new CheckConnectionHealthCommand(id), ct);

        return result.IsSuccess ? Results.Ok(result.Value) : Results.UnprocessableEntity(new { error = result.Error });
    }

    private static async Task<IResult> ActivateAsync(
        Guid id, ISender sender, MessagingChannels channels, ITenantContext tenant, CancellationToken ct)
    {
        var existing = await channels.GetAsync(tenant.CurrentTenantId, id, ct);
        if (existing is null) return NotFound();

        var result = await sender.Send(new ActivateConnectionCommand(id), ct);

        if (result.IsSuccess)
        {
            await channels.ForgetAsync(existing.ChannelAddress, ct);
            return Results.NoContent();
        }

        return result.Error is IntegrationErrors.MessagingNumberAlreadyConnected or IntegrationErrors.ConcurrencyConflict
            ? Results.Conflict(new { error = result.Error })
            : Results.UnprocessableEntity(new { error = result.Error });
    }

    private static async Task<IResult> DeactivateAsync(
        Guid id, ISender sender, MessagingChannels channels, ITenantContext tenant, CancellationToken ct)
    {
        var existing = await channels.GetAsync(tenant.CurrentTenantId, id, ct);
        if (existing is null) return NotFound();

        var result = await sender.Send(new DeactivateConnectionCommand(id), ct);

        if (result.IsSuccess) await channels.ForgetAsync(existing.ChannelAddress, ct);

        return result.IsSuccess ? Results.NoContent() : Results.UnprocessableEntity(new { error = result.Error });
    }

    /// <summary>
    /// A new token, stored, then handed to the provider together with the URL.
    ///
    /// <para>
    /// <b>Stored BEFORE the provider is told</b>, and that order is the safe one: if the provider
    /// call fails, deliveries keep arriving with the old token and are refused until the
    /// administrator runs this again — visible, and repaired by the same button. The opposite
    /// order would leave a window where the provider sends a token the vault does not hold yet.
    /// </para>
    /// </summary>
    private static async Task<IResult> ConfigureWebhookAsync(
        Guid id,
        HttpContext http,
        MessagingChannels channels,
        ISender sender,
        IConfiguration config,
        ITenantContext tenant,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var connection = await channels.GetAsync(tenant.CurrentTenantId, id, ct);
        if (connection is null) return NotFound();

        var baseUrl = config[WebhookPublicBaseUrlKey] is { Length: > 0 } configured
            ? configured.TrimEnd('/')
            : ExternalOrigin(http.Request);

        if (!Uri.TryCreate($"{baseUrl}/integration/webhooks/whatsapp/{MessagingWebhookEndpoints.SegmentOf(connection.Kind)}",
                UriKind.Absolute, out var url))
        {
            return Results.UnprocessableEntity(new { error = IntegrationErrors.MessagingPublicUrlMissing });
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var stored = await sender.Send(
            new SetConnectionSecretCommand(id, IntegrationSecrets.WebhookSecretName, token), ct);

        if (stored.IsFailure) return Results.UnprocessableEntity(new { error = stored.Error });

        var port = channels.PortFor(connection, IntegrationCapability.MessageEvents);

        if (port.IsFailure) return Results.UnprocessableEntity(new { error = port.Code, detail = port.Detail });

        var configuredAtProvider = await port.Value.ConfigureWebhookAsync(
            connection,
            url,
            new Dictionary<string, string> { [MessagingWebhookEndpoints.TokenHeader] = token },
            ct);

        if (configuredAtProvider.IsFailure)
        {
            loggerFactory.CreateLogger(typeof(WhatsAppChannelEndpoints)).LogWarning(
                "Configuring the WhatsApp webhook of connection {ConnectionId} failed: {Code}.",
                id, configuredAtProvider.Code);

            return Results.UnprocessableEntity(new { error = configuredAtProvider.Code, detail = configuredAtProvider.Detail });
        }

        return Results.Ok(new ConfigureWhatsAppWebhookResponse(url.AbsoluteUri));
    }

    // ── Development simulator ────────────────────────────────────────────────────────────────

    private static Task<IResult> SimulateInboundAsync(
        Guid id,
        SimulateWhatsAppInboundRequest request,
        IHostEnvironment environment,
        IntegrationDbContext db,
        MessagingChannels channels,
        TelephonyPayloadProtector payloads,
        ISecretsModule secrets,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        ITenantContext tenant,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
        => SimulateAsync(
            id, environment, db, channels, payloads, secrets, hangfire, clock, tenant, loggerFactory, ct,
            (address, now) => CloudApiWebhook.BuildInboundText(
                address,
                request.FromE164.StartsWith('+') ? request.FromE164 : "+" + request.FromE164,
                request.ProfileName,
                request.Text,
                "wamid.SIMULATED" + Guid.NewGuid().ToString("N"),
                now));

    private static Task<IResult> SimulateStatusAsync(
        Guid id,
        SimulateWhatsAppStatusRequest request,
        IHostEnvironment environment,
        IntegrationDbContext db,
        MessagingChannels channels,
        TelephonyPayloadProtector payloads,
        ISecretsModule secrets,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        ITenantContext tenant,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
        => SimulateAsync(
            id, environment, db, channels, payloads, secrets, hangfire, clock, tenant, loggerFactory, ct,
            (address, now) => CloudApiWebhook.BuildStatus(address, request.ProviderMessageId, request.Status, now));

    /// <summary>
    /// Builds the body, then calls the REAL door with the connection's own stored token — so a
    /// simulated delivery is verified, deduplicated and queued exactly like a real one.
    /// </summary>
    private static async Task<IResult> SimulateAsync(
        Guid id,
        IHostEnvironment environment,
        IntegrationDbContext db,
        MessagingChannels channels,
        TelephonyPayloadProtector payloads,
        ISecretsModule secrets,
        IBackgroundJobClient hangfire,
        TimeProvider clock,
        ITenantContext tenant,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        Func<string, DateTimeOffset, string> build)
    {
        if (!environment.IsDevelopment()) return Results.NotFound();

        var connection = await channels.GetAsync(tenant.CurrentTenantId, id, ct);
        if (connection is null || connection.ChannelAddress is null) return NotFound();

        if (!connection.IsActive)
            return Results.UnprocessableEntity(new { error = IntegrationErrors.MessagingChannelInactive });

        var token = await secrets.GetValueAsync(IntegrationSecrets.WebhookSecretKey(connection.TenantId, connection.Id), ct);

        if (string.IsNullOrWhiteSpace(token))
            return Results.UnprocessableEntity(new { error = IntegrationErrors.CredentialMissing, detail = "Run « Configurer le webhook » first." });

        var body = System.Text.Encoding.UTF8.GetBytes(build(connection.ChannelAddress, clock.GetUtcNow()));

        var accepted = await MessagingWebhookEndpoints.AcceptAsync(
            MessagingWebhookEndpoints.SegmentOf(connection.Kind),
            body,
            token,
            signature: null,
            db, channels, payloads, secrets, hangfire, clock,
            loggerFactory.CreateLogger(typeof(WhatsAppChannelEndpoints)),
            ct);

        return accepted ? Results.Accepted() : Results.UnprocessableEntity(new { error = "SIMULATION_REFUSED" });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static (IntegrationKind Kind, ConnectionSettings Settings) SettingsOf(SaveWhatsAppChannelRequest request)
        => request.Simulated
            ? (IntegrationKind.Fake, new FakeSettings
            {
                Family = IntegrationFamily.Messaging,
                PhoneNumberId = request.PhoneNumberId?.Trim(),
            })
            : (IntegrationKind.Dialog360, new Dialog360Settings
            {
                PhoneNumberId = request.PhoneNumberId?.Trim() ?? string.Empty,
                WabaId = string.IsNullOrWhiteSpace(request.WabaId) ? null : request.WabaId.Trim(),
                DisplayPhoneNumber = request.DisplayPhoneNumber?.Trim() ?? string.Empty,
                AgencyId = request.AgencyId == Guid.Empty ? null : request.AgencyId,
                ApiBaseUrl = string.IsNullOrWhiteSpace(request.ApiBaseUrl) ? null : request.ApiBaseUrl.Trim(),
            });

    /// <summary>The origin the caller used, behind a TLS-terminating proxy included.</summary>
    private static string ExternalOrigin(HttpRequest request)
    {
        var scheme = request.Headers["X-Forwarded-Proto"].ToString() is { Length: > 0 } proto
            ? proto.Split(',')[0].Trim()
            : request.Scheme;

        var host = request.Headers["X-Forwarded-Host"].ToString() is { Length: > 0 } forwarded
            ? forwarded.Split(',')[0].Trim()
            : request.Host.Value;

        return $"{scheme}://{host}{request.PathBase}";
    }

    private static IResult NotFound() => Results.NotFound(new { error = IntegrationErrors.ConnectionNotFound });
}

/// <param name="Status"><c>Active</c>, <c>Pending</c> (not yet tested or activated) or <c>Error</c> (last test failed).</param>
/// <param name="ApiKeyHint">The vault's masked hint of the stored key — enough to tell two apart, never to use one.</param>
/// <param name="Simulated">An in-memory number (Development only).</param>
internal sealed record WhatsAppChannelDto(
    Guid Id,
    string Name,
    string Kind,
    bool Simulated,
    string? PhoneNumberId,
    string? WabaId,
    string? DisplayPhoneNumber,
    Guid? AgencyId,
    string? ApiBaseUrl,
    bool IsActive,
    string Status,
    DateTimeOffset? LastHealthAt,
    bool? LastHealthStatus,
    string? LastHealthDetail,
    bool HasApiKey,
    string? ApiKeyHint,
    bool HasWebhookToken,
    uint Version)
{
    public static WhatsAppChannelDto From(IntegrationConnection c, SecretHint? apiKey, bool hasWebhookToken)
    {
        var d = c.Settings as Dialog360Settings;

        return new WhatsAppChannelDto(
            c.Id,
            c.Name,
            c.Kind.ToString(),
            c.Kind == IntegrationKind.Fake,
            c.ChannelAddress,
            d?.WabaId,
            MessagingChannels.DisplayNumberOf(c),
            MessagingChannels.AgencyOf(c),
            d?.ApiBaseUrl,
            c.IsActive,
            c.IsActive ? "Active" : c.LastHealthStatus == false ? "Error" : "Pending",
            c.LastHealthAt,
            c.LastHealthStatus,
            c.LastHealthDetail,
            // A simulated number needs no key: the double never calls anybody.
            apiKey is not null || c.Kind == IntegrationKind.Fake,
            apiKey?.MaskedValue,
            hasWebhookToken,
            c.Version);
    }
}

/// <param name="Simulated">Development only: an in-memory number that sends nothing.</param>
internal sealed record SaveWhatsAppChannelRequest(
    string Name,
    string? PhoneNumberId,
    string? WabaId,
    string? DisplayPhoneNumber,
    Guid? AgencyId,
    string? ApiBaseUrl,
    uint? ExpectedVersion = null,
    bool Simulated = false);

internal sealed record SetWhatsAppApiKeyRequest([property: SensitiveData] string ApiKey);

/// <param name="WebhookUrl">The address 360dialog now delivers to.</param>
internal sealed record ConfigureWhatsAppWebhookResponse(string WebhookUrl);

internal sealed record SimulateWhatsAppInboundRequest(string FromE164, string Text, string? ProfileName = null);

/// <param name="Status"><c>sent</c>, <c>delivered</c>, <c>read</c> or <c>failed</c>.</param>
internal sealed record SimulateWhatsAppStatusRequest(string ProviderMessageId, string Status);
