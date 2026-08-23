namespace Sankore.Modules.Notifications;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Notifications.Features.DeliveryLogs;
using Sankore.Modules.Notifications.Features.EmailTemplates;
using Sankore.Modules.Notifications.Features.Webhooks;
using Sankore.Modules.Notifications.Features.Webhooks.Parsers;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Modules.Notifications.Infrastructure.Processor;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Modules.Notifications.Infrastructure.Rendering;
using Sankore.Modules.Notifications.Infrastructure.Senders;
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
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "notifications")));

        services.AddScoped<INotificationsModule, NotificationsModuleFacade>();

        // Provider resolution: cache-aside via IDistributedCache (Redis in prod, in-memory in tests)
        services.AddScoped<IEmailProviderResolver, CachedEmailProviderResolver>();

        // Template rendering: Scriban-based with DB lookup (tenant → platform → en fallback)
        services.AddScoped<ITemplateRenderer, ScribanTemplateRenderer>();

        // Email transport: stub logs intent; replaced by real adapters (SES/Postmark/SendGrid) later
        services.AddSingleton<IEmailSender, StubEmailSender>();

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
        services.AddValidatorsFromAssembly(typeof(NotificationsModule).Assembly);

        return services;
    }

    /// <summary>Runs EF migrations. Call once at startup inside a scoped block in Program.cs.</summary>
    public static async Task InitializeAsync(IServiceProvider sp)
    {
        var db = sp.GetRequiredService<NotificationsDbContext>();
        await db.Database.MigrateAsync();
    }

    public static IEndpointRouteBuilder MapNotificationsModuleEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapEmailTemplatesEndpoints();
        app.MapWebhooksEndpoints();
        app.MapDeliveryLogsEndpoints();
        return app;
    }
}
