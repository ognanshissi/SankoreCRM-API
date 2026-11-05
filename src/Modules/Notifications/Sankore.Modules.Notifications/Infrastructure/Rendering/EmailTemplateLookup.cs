namespace Sankore.Modules.Notifications.Infrastructure.Rendering;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Domain;
using Sankore.Shared.Kernel;

/// <summary>The one search for a template: what the renderer uses is what a caller can ask about.</summary>
internal static class EmailTemplateLookup
{
    public static async Task<EmailTemplate?> FindAsync(
        NotificationsDbContext db, Guid tenantId, string key, string locale, CancellationToken ct)
    {
        // 1. Tenant-specific + requested locale
        var found = await db.EmailTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.TenantId == tenantId && t.TemplateKey == key && t.Locale == locale && t.IsActive,
                ct);

        if (found is not null) return found;

        // 2. Platform default + requested locale
        found = await db.EmailTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.TenantId == null && t.TemplateKey == key && t.Locale == locale && t.IsActive,
                ct);

        if (found is not null) return found;

        // 3. Platform default + "fr" fallback (system default locale). The caller's locale is
        //    already normalised, so this comparison and the two above agree on what matches.
        if (locale != LanguageCode.Default)
        {
            found = await db.EmailTemplates
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    t => t.TenantId == null && t.TemplateKey == key && t.Locale == LanguageCode.Default && t.IsActive,
                    ct);
        }

        return found;
    }
}
