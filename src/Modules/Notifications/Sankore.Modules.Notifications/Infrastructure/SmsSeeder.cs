namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure.Sms;

/// <summary>
/// Seeds the platform-default SMS bodies. Idempotent — only inserts what is absent, like
/// <see cref="NotificationsSeeder"/>.
///
/// <para>
/// <b>Every body here is written to fit ONE GSM-7 segment, and that is a cost decision, not a
/// style one.</b> 160 characters is one message; 161 is two, and a single accented character drops
/// the budget to 70 and makes a short message cost two or three. So the French bodies carry no
/// accents — "verification" and not "vérification" — which looks like a typo and is not. A tenant
/// is free to fork one and write it their way; <see cref="SmsSegments"/> is what tells them what
/// that costs, and the write slice returns the count.
/// </para>
/// </summary>
internal static class SmsSeeder
{
    /// <summary>
    /// The one-time code of KRM-B-12. Carries the code and nothing else that identifies anybody:
    /// a text message sits unlocked on a lock screen, and naming the institution and the customer
    /// beside a code is how a stolen phone becomes an enrolment.
    /// </summary>
    internal const string OtpTemplateKey = "kyc.remote-otp";

    public static async Task SeedAsync(
        NotificationsDbContext db, ILogger logger, CancellationToken ct = default)
    {
        await SeedAsync(
            db, logger, ct,
            key: OtpTemplateKey,
            fr: "Votre code de verification est {{ code }}. Il expire dans {{ minutes }} minutes. "
              + "Ne le communiquez a personne.",
            en: "Your verification code is {{ code }}. It expires in {{ minutes }} minutes. "
              + "Do not share it with anyone.");

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedAsync(
        NotificationsDbContext db,
        ILogger logger,
        CancellationToken ct,
        string key,
        string fr,
        string en)
    {
        var existing = await db.SmsTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TemplateKey == key && t.TenantId == null)
            .Select(t => t.Locale)
            .ToListAsync(ct);

        foreach (var (locale, body) in new[] { ("fr", fr), ("en", en) })
        {
            if (existing.Contains(locale)) continue;

            db.SmsTemplates.Add(SmsTemplate.Create(
                tenantId: null,
                templateKey: key,
                locale: locale,
                version: 1,
                body: body,
                isSystem: true));

            var segments = SmsSegments.Count(body);

            // Logged at WARNING when a seeded body does not fit one segment: these are the
            // platform's own messages, and one that costs double on every tenant is a mistake in
            // this file rather than a tenant's choice.
            if (segments > 1)
            {
                logger.LogWarning(
                    "SmsSeeder: platform template '{Key}' [{Locale}] is {Segments} segments — "
                    + "every message will be billed {Segments} times",
                    key, locale, segments, segments);
            }
            else
            {
                logger.LogInformation(
                    "SmsSeeder: seeded platform SMS template '{Key}' [{Locale}]", key, locale);
            }
        }
    }
}
