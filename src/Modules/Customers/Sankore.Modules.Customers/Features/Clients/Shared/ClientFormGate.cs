namespace Sankore.Modules.Customers.Features.Clients.Shared;

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Shared.Kernel;

/// <summary>
/// The client form through <see cref="EntityFormGate"/> — see there for the failure posture.
/// </summary>
/// <remarks>
/// <paramref name="administration"/> is nullable because the handlers take it as an optional
/// dependency (their unit tests build them by hand). Without it a write carrying custom values is
/// refused like an outage, and one without goes through unchecked.
/// </remarks>
internal static class ClientFormGate
{
    /// <summary>
    /// The keys of the custom client fields that may be returned in clear by a read — every custom
    /// key NOT marked sensitive. Null when the form cannot be read, and the caller then withholds
    /// every custom value: a detail screen missing a few values is a nuisance, a sensitive one
    /// shown in clear because M12 blinked is a disclosure.
    /// </summary>
    public static async Task<IReadOnlySet<string>?> ShowableKeysAsync(
        IAdministrationModule? administration, Guid tenantId, CancellationToken ct)
    {
        if (administration is null) return null;

        try
        {
            using var scope = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
            var form = await administration.GetEntityFormAsync(tenantId, FormEntity.Client, ct);

            return form?.Fields
                .Where(f => !f.IsNative && !f.IsSensitive)
                .Select(f => f.Key)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public static Task<Result<string?>> ApplyAsync(
        IAdministrationModule? administration,
        ILogger? logger,
        Guid tenantId,
        string? currentJson,
        IReadOnlyDictionary<string, JsonElement>? changes,
        IReadOnlyDictionary<string, string?> nativeValues,
        bool enforceRequired,
        bool isCreate,
        CancellationToken ct)
    {
        if (administration is null)
        {
            return Task.FromResult(changes is { Count: > 0 }
                ? Result.Fail<string?>("CUSTOM_FIELDS_UNAVAILABLE")
                : Result.Ok(currentJson));
        }

        return EntityFormGate.ApplyAsync(
            administration, FormEntity.Client, tenantId, currentJson, changes, nativeValues,
            enforceRequired, isCreate,
            ex => logger?.LogWarning(ex,
                "Client form unreadable for tenant {TenantId}: required fields not checked on this write", tenantId),
            ct);
    }
}
