namespace Sankore.Modules.Administration.PublicApi.Forms;

using System.Text.Json;
using System.Transactions;
using Sankore.Shared.Kernel;

/// <summary>
/// Where a write of a lead, a client or a product meets the tenant's form (dynamic forms, lot 1):
/// custom values checked and merged, and the native fields the tenant made required enforced on
/// the interactive paths. One body for the three modules, so they cannot disagree on what an
/// outage means.
///
/// <para>
/// <b>Failure posture.</b> The form is M12's, read across modules. When it cannot be read: a write
/// carrying custom values is REFUSED (<c>CUSTOM_FIELDS_UNAVAILABLE</c>) — they cannot be checked,
/// and storing them unchecked is how a typo becomes data; a write with no custom values goes
/// through and only the required-field check is skipped, because losing a prospect on the phone to
/// an M12 blip is the one outcome a capture must never have.
/// </para>
///
/// <para>
/// <b>No call at all</b> when there is nothing to check — every import row, web ingest and
/// scheduled pull, which carry no custom value and enforce no requirement.
/// </para>
/// </summary>
public static class EntityFormGate
{
    /// <param name="onUnavailable">Told when the form could not be read and the write went on anyway — log it.</param>
    public static async Task<Result<string?>> ApplyAsync(
        IAdministrationModule administration,
        FormEntity entity,
        Guid tenantId,
        string? currentJson,
        IReadOnlyDictionary<string, JsonElement>? changes,
        IReadOnlyDictionary<string, string?> nativeValues,
        bool enforceRequired,
        bool isCreate,
        Action<Exception>? onUnavailable,
        CancellationToken ct)
    {
        var hasChanges = changes is { Count: > 0 };
        if (!hasChanges && !enforceRequired)
            return Result.Ok(currentJson);

        EntityFormDto? form;
        try
        {
            // Suppressed: TransactionBehavior holds an ambient scope, and M12's DbContext enlisting
            // in it would promote to a distributed transaction Npgsql refuses at run time.
            using var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            form = await administration.GetEntityFormAsync(tenantId, entity, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (hasChanges)
                return Result.Fail<string?>("CUSTOM_FIELDS_UNAVAILABLE");

            onUnavailable?.Invoke(ex);
            return Result.Ok(currentJson);
        }

        // A test double answers null; a real M12 never does.
        form ??= new EntityFormDto(entity, 0, [], []);

        if (enforceRequired && isCreate)
        {
            var missing = CustomFieldValues.MissingRequiredNatives(form, nativeValues);
            if (missing.Count > 0)
                return Result.Fail<string?>($"FIELD_REQUIRED:{missing[0]}");
        }

        return CustomFieldValues.Apply(form, currentJson, changes, nativeValues, enforceRequired, isCreate);
    }

    /// <summary>
    /// For the paths whose sender cannot correct anything — a web form, a webhook, a pull, a
    /// conversion: the values the form accepts are kept, the others set aside and returned. When the
    /// form cannot be read, every value is set aside: storing them unchecked is how a typo becomes
    /// data, and refusing the record would lose a prospect.
    /// </summary>
    public static async Task<(string? Json, IReadOnlyList<string> Rejected)> ApplyBestEffortAsync(
        IAdministrationModule administration,
        FormEntity entity,
        Guid tenantId,
        string? currentJson,
        IReadOnlyDictionary<string, JsonElement>? changes,
        IReadOnlyDictionary<string, string?>? nativeValues,
        Action<Exception>? onUnavailable,
        CancellationToken ct)
    {
        if (changes is not { Count: > 0 }) return (currentJson, []);

        EntityFormDto? form;
        try
        {
            using var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            form = await administration.GetEntityFormAsync(tenantId, entity, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onUnavailable?.Invoke(ex);
            return (currentJson, changes.Keys.ToList());
        }

        return CustomFieldValues.ApplyBestEffort(form ?? new EntityFormDto(entity, 0, [], []), currentJson, changes, nativeValues);
    }
}
