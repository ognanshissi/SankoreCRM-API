namespace Sankore.Modules.Notifications;

using FluentValidation;
using MediatR;
using Sankore.Modules.Notifications.Features.InApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Features.DeliveryLogs;
using Sankore.Modules.Notifications.Features.EmailTemplates;
using Sankore.Modules.Notifications.Features.SmsTemplates;
using Sankore.Modules.Notifications.Features.SmsWebhooks;
using Sankore.Modules.Notifications.Features.Webhooks;
using Sankore.Modules.Notifications.Features.Webhooks.Parsers;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Modules.Notifications.Infrastructure.Processor;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Modules.Notifications.Infrastructure.Rendering;
using Sankore.Modules.Notifications.Infrastructure.Senders;
using Microsoft.AspNetCore.Http;
using Sankore.Modules.Notifications.Features.TestSend;
using Sankore.Modules.Notifications.Infrastructure.Senders.Brevo;
using Sankore.Modules.Notifications.Infrastructure.Senders.Smtp;
using Sankore.Modules.Notifications.Infrastructure.Sms;
using Sankore.Modules.Notifications.Infrastructure.Sms.Senders;
using Sankore.Modules.Notifications.PublicApi;

/// <summary>
/// Composition root of the Notifications module.
/// The ONE public static class the Bootstrapper calls — everything else is internal.
/// </summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddDbContext<NotificationsDbContext>(opt =>
            opt.UseNpgsql(
                config.GetConnectionString("Database"),
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"))
                .UseSnakeCaseNamingConvention());

        services.AddScoped<INotificationsModule, NotificationsModuleFacade>();

        // Provider resolution: cache-aside via IDistributedCache (Redis in prod, in-memory in tests)
        services.AddScoped<IEmailProviderResolver, CachedEmailProviderResolver>();

        // Template rendering: Scriban-based with DB lookup (tenant → platform → en fallback)
        services.AddScoped<ITemplateRenderer, ScribanTemplateRenderer>();

        // Email transport: keyed senders per provider type, routed by CompositeEmailSender
        // "stub" is the fallback for provider types with no sender implementation yet
        // (Ses / Postmark / SendGrid) — it logs instead of throwing at send time.
        // Scoped, not singleton: the senders resolve the tenant's credential through
        // ISecretsModule, which is scoped because it owns a DbContext.
        services.AddKeyedScoped<IEmailSender, StubEmailSender>("stub");
        services.AddKeyedScoped<IEmailSender, SmtpEmailSender>("smtp");
        services.AddKeyedScoped<IEmailSender, BrevoEmailSender>("brevo");
        services.AddScoped<IEmailSender, CompositeEmailSender>();
        services.AddScoped<INotificationCredentials, VaultNotificationCredentials>();
        services.AddHttpClient(BrevoEmailSender.HttpClientName);

        // ── The SMS channel ─────────────────────────────────────────────────
        // Mirrors the email registrations above, with two deliberate differences: there is NO
        // stub fallback — a provider this module does not implement must fail loudly rather than
        // log and report success, because the alternative on a per-message channel is silence
        // that still costs money — and each provider gets its own named HTTP client, since an SMS
        // send is the slower of the two and may want its own timeout.
        services.AddScoped<ISmsProviderResolver, CachedSmsProviderResolver>();
        services.AddScoped<ISmsTemplateRenderer, SmsTemplateRenderer>();
        services.AddScoped<ISmsCredentials, VaultSmsCredentials>();

        services.AddKeyedScoped<ISmsSender, BrevoSmsSender>("brevo");
        services.AddKeyedScoped<ISmsSender, TwilioSmsSender>("twilio");
        services.AddScoped<ISmsSender, CompositeSmsSender>();

        services.AddHttpClient(BrevoSmsSender.HttpClientName);
        services.AddHttpClient(TwilioSmsSender.HttpClientName);

        services.Configure<SmsOutboxProcessorOptions>(
            config.GetSection(SmsOutboxProcessorOptions.SectionName));
        services.AddHostedService<SmsOutboxProcessor>();

        // Derives the per-tenant Brevo callback token from the platform key — Brevo does not sign
        // its SMS events, so the URL carries one instead. See SmsWebhookSignatures.
        services.AddScoped<Features.SmsWebhooks.SmsWebhookSignatures>();

        // SMTP options — bind from Notifications:Smtp in appsettings / user-secrets
        services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.SectionName));

        // Outbox processor options + background service
        services.Configure<EmailOutboxProcessorOptions>(
            config.GetSection(EmailOutboxProcessorOptions.SectionName));
        services.AddHostedService<EmailOutboxProcessor>();

        // Webhook parsers — registered as IWebhookParser so handler receives IEnumerable<IWebhookParser>
        services.AddScoped<IWebhookParser, SnsWebhookParser>();
        services.AddScoped<IWebhookParser, PostmarkWebhookParser>();
        services.AddScoped<IWebhookParser, SendGridWebhookParser>();

        // IHttpClientFactory used by webhook endpoint for SNS subscription confirmation
        services.AddHttpClient();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(NotificationsModule).Assembly));
        // includeInternalTypes: validators here are mostly `internal sealed`, and the
        // assembly scan skips those by default — without this they never run.
        services.AddValidatorsFromAssembly(typeof(NotificationsModule).Assembly, includeInternalTypes: true);

        return services;
    }

    /// <summary>Runs EF migrations and seeds platform templates. Call once at startup inside a scoped block in Program.cs.</summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<NotificationsDbContext>();
        await db.Database.MigrateAsync();

        var logger = sp.GetRequiredService<ILogger<NotificationsDbContext>>();
        await NotificationsSeeder.SeedAsync(db, logger);
        await SmsSeeder.SeedAsync(db, logger);
    }

    /// <summary>
    /// The SMS delivery callbacks, mapped SEPARATELY by the host under <c>api/v1/public</c>.
    ///
    /// <para>
    /// Not part of <see cref="MapNotificationsModuleEndpoints"/>, and the reason is
    /// <c>TenantResolutionMiddleware</c>: it exempts <c>/api/v1/public</c> and nothing else, and a
    /// provider's callback carries no tenant header and never will. Mapped under the module's own
    /// group these endpoints would be refused before reaching their own verification — which is
    /// the sort of failure that looks like a broken integration at the provider's end.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapNotificationsPublicEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapSmsWebhooksEndpoints();
        return app;
    }

    public static IEndpointRouteBuilder MapNotificationsModuleEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapEmailTemplatesEndpoints();
        app.MapSmsTemplatesEndpoints();
        app.MapWebhooksEndpoints();
        app.MapDeliveryLogsEndpoints();
        app.MapGroup("notification-settings").WithTags("Notification Settings").MapSendTestEmail();
        // La cloche (WA-FE-10) : les notifications in-app de l'utilisateur connecté.
        app.MapInAppNotificationsEndpoints();

        return app;
    }
}
