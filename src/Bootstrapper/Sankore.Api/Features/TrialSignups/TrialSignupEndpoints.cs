using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

namespace Sankore.Api.Features.TrialSignups;

/// <summary>
/// <c>/api/v1/public/trial-signups</c> — the website's free-trial form (lot 2), relayed to the tenant
/// registry which decides it (<see cref="TrialSignupRelay"/>). Under <c>public</c>
/// because TenantResolutionMiddleware exempts that prefix: there is no institution yet. Anonymous,
/// each route with its own rate limit (the "api" policy partitions on a JWT subject the visitor has
/// not got).
/// </summary>
public static class TrialSignupEndpoints
{
    /// <summary>The form itself: <see cref="TrialSignupOptions.RequestsPerHourPerIp"/> per IP and hour.</summary>
    public const string SubmitRateLimit = "trial-signup";

    /// <summary>Availability as you type, and the confirmation link: 30 per IP and minute.</summary>
    public const string LookupRateLimit = "trial-lookup";

    public sealed record ConfirmRequest(string? Token);

    public static void MapTrialSignupEndpoints(this RouteGroupBuilder publicGroup)
    {
        var group = publicGroup.MapGroup("trial-signups").WithTags("Free trial").AllowAnonymous();

        group.MapGet("/slug-availability", ([FromQuery] string? slug, TrialSignupRelay relay, CancellationToken ct) =>
                relay.CheckSlugAsync(slug, ct))
            .RequireRateLimiting(LookupRateLimit)
            .WithName("CheckTrialAddress")
            .WithSummary("Whether a free-trial address can be chosen: Available, TRIAL_SLUG_INVALID, TRIAL_SLUG_RESERVED or TRIAL_SLUG_TAKEN")
            .WithOpenApi();

        group.MapPost("/", (JsonElement form, HttpContext http, TrialSignupRelay relay, CancellationToken ct) =>
                relay.SubmitAsync(form, TrialSignupRelay.ClientIp(http), http.Request.Headers.UserAgent.ToString(), ct))
            .RequireRateLimiting(SubmitRateLimit)
            .WithName("SubmitTrialSignup")
            .WithSummary("Ask for a free trial; a confirmation link is mailed")
            .WithDescription("Decided by the tenant registry, relayed here. 202 CONFIRMATION_SENT — also when the "
                + "address already administers a CRM, which is said by e-mail only. 400 TRIAL_SIGNUP_INVALID "
                + "(errors by field), TRIAL_CAPTCHA_FAILED, TRIAL_SLUG_INVALID, TRIAL_SLUG_RESERVED; "
                + "409 TRIAL_SLUG_TAKEN; 429; 503.")
            .WithOpenApi();

        group.MapPost("/confirm", (ConfirmRequest body, TrialSignupRelay relay, CancellationToken ct) =>
                relay.ConfirmAsync(body.Token, ct))
            .RequireRateLimiting(LookupRateLimit)
            .WithName("ConfirmTrialSignup")
            .WithSummary("Confirm the e-mail address of a free-trial request with the token of its link")
            .WithOpenApi();
    }
}

/// <summary>
/// Every minute: tells requesters the platform's decision (lot 2). A refusal is mailed at once; an
/// approval waits until the institution's root account exists — the starting-data load creates it
/// within a minute or two — because the « Votre CRM est prêt » e-mail carries the link to choose its
/// password. A decision is marked as told only once its e-mail is queued, so a failed run is retried.
/// </summary>
public sealed class TrialSignupDecisionsJob(
    ITrialSignupRegistry registry,
    IServiceScopeFactory scopeFactory,
    ITenantStore tenantStore,
    INotificationsModule notifications,
    ILogger<TrialSignupDecisionsJob> logger)
{
    public const string RecurringJobId = "trial-signup-decisions";

    /// <summary>An approval whose account never appears (a failed load) is logged, not retried for ever silently.</summary>
    internal static readonly TimeSpan StaleApproval = TimeSpan.FromHours(1);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var decisions = await registry.DecisionsToNotifyAsync(ct);
        if (!decisions.IsSuccess || decisions.Value is not { Count: > 0 } list) return;

        foreach (var decision in list)
        {
            try
            {
                var told = decision.Status switch
                {
                    "Rejected" => await TellRejectedAsync(decision, ct),
                    "Approved" => await TellReadyAsync(decision, ct),
                    _ => false,
                };
                if (told) await registry.MarkDecisionNotifiedAsync(decision.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not tell the decision on trial request {SignupId}; next run retries", decision.Id);
            }
        }
    }

    private async Task<bool> TellRejectedAsync(RegistrySignupDecision decision, CancellationToken ct)
    {
        var queued = await notifications.QueueEmailAsync(new QueueEmailRequest(
            TemplateKey: "trial.signup-rejected", RecipientEmail: decision.Email, RecipientName: decision.FirstName,
            Module: "TrialSignup", Locale: LanguageCode.Normalize(decision.Locale),
            TemplateData: new Dictionary<string, object>
            {
                ["first_name"] = decision.FirstName,
                ["institution_name"] = decision.InstitutionName,
                ["reason"] = decision.RejectionReason ?? "",
            },
            IdempotencyKey: $"trial.signup-decision:{decision.Id}", TenantId: TrialSignupRelay.PlatformTenantId), ct);
        return queued.IsSuccess || queued.Error == "DUPLICATE_IDEMPOTENCY_KEY";
    }

    private async Task<bool> TellReadyAsync(RegistrySignupDecision decision, CancellationToken ct)
    {
        if (decision.TenantId is not { } tenantId) return false;

        RootPasswordSetupLink? link;
        using (BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM"))
        await using (var scope = scopeFactory.CreateAsyncScope())
            link = await scope.ServiceProvider.GetRequiredService<IRootPasswordSetup>().IssueAsync(tenantId, ct);

        if (link is null)
        {
            if (DateTimeOffset.UtcNow - decision.DecidedAt > StaleApproval)
                logger.LogWarning("Trial institution {TenantId} still has no root account an hour after its approval: "
                    + "check its starting-data load on the platform", tenantId);
            return false;
        }

        var tenant = await tenantStore.GetAsync(tenantId, ct);
        var locale = LanguageCode.Normalize(decision.Locale);
        var crmUrl = (tenant?.Fqdn ?? decision.Fqdn ?? "").TrimEnd('/');

        var queued = await notifications.QueueEmailAsync(new QueueEmailRequest(
            TemplateKey: "trial.ready", RecipientEmail: link.Email, RecipientName: decision.FirstName,
            Module: "TrialSignup", Locale: locale,
            TemplateData: new Dictionary<string, object>
            {
                ["first_name"] = decision.FirstName,
                ["institution_name"] = tenant?.Name ?? decision.InstitutionName,
                ["crm_url"] = crmUrl,
                ["activation_url"] = link.ActivationUrl ?? "",
                ["email"] = link.Email,
                ["trial_end"] = TrialSignupRelay.TrialEnd(tenant?.TrialExpiresAt, locale),
                ["expires_days"] = link.ExpiresAt is { } at ? Math.Max(1, (int)Math.Round((at - DateTimeOffset.UtcNow).TotalDays)) : 7,
            },
            IdempotencyKey: $"trial.signup-decision:{decision.Id}", TenantId: tenantId), ct);
        return queued.IsSuccess || queued.Error == "DUPLICATE_IDEMPOTENCY_KEY";
    }
}
