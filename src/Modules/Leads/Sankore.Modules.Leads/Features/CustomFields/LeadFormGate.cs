namespace Sankore.Modules.Leads.Features.CustomFields;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Shared.Kernel;

/// <summary>The lead form through <see cref="EntityFormGate"/> — see there for the failure posture.</summary>
internal static class LeadFormGate
{
    public static Task<Result<string?>> ApplyAsync(
        IAdministrationModule administration,
        ILogger logger,
        Guid tenantId,
        string? currentJson,
        IReadOnlyDictionary<string, JsonElement>? changes,
        IReadOnlyDictionary<string, string?> nativeValues,
        bool enforceRequired,
        bool isCreate,
        CancellationToken ct)
        => EntityFormGate.ApplyAsync(
            administration, FormEntity.Lead, tenantId, currentJson, changes, nativeValues,
            enforceRequired, isCreate,
            ex => logger.LogWarning(ex,
                "Lead form unreadable for tenant {TenantId}: required fields not checked on this write", tenantId),
            ct);
}
