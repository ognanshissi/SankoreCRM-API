namespace Sankore.Modules.Customers.Features.Clients.Shared;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// A client that already carries the value being submitted. Everything here is
/// non-sensitive on purpose: the operator needs to recognise the existing record
/// ("CLI-ABJ-2026-000042 — KONE Awa"), not to read its protected fields.
/// </summary>
/// <param name="Reason">
/// One of the <see cref="CustomerErrors"/> duplicate codes, so the front-end
/// localises the message instead of receiving English prose.
/// </param>
public sealed record DuplicateHit(
    Guid ClientId,
    string ClientNumber,
    string DisplayName,
    Guid AgencyId,
    string Reason);

/// <summary>
/// Exact-match lookups over the blind-index columns — the ONLY way this module
/// answers "does a client with this document / phone already exist?".
///
/// Every method compares HMAC values. Nothing is ever decrypted: a probe that had
/// to decrypt would mean loading and unwrapping the protected column of every
/// client in the tenant, which is both O(n) and a wholesale exposure of PII for a
/// question that a single indexed equality answers. The handler tests assert that
/// <c>IFieldEncryptor.Decrypt</c> is never called during duplicate detection.
/// </summary>
public interface IDuplicateProbe
{
    /// <summary>
    /// The client holding this identity-document blind index, or <c>null</c>.
    /// A hit is a HARD block on creation (<c>DUPLICATE_IDENTITY_DOCUMENT</c>).
    /// </summary>
    Task<DuplicateHit?> FindByIdentityDocumentAsync(
        Guid tenantId, string blindIndex, Guid? excludeClientId, CancellationToken ct);

    /// <summary>
    /// The legal client holding this registration-number (RCCM) blind index, or
    /// <c>null</c>. A hit is a HARD block (<c>DUPLICATE_REGISTRATION_NUMBER</c>).
    /// </summary>
    Task<DuplicateHit?> FindByRegistrationNumberAsync(
        Guid tenantId, string blindIndex, Guid? excludeClientId, CancellationToken ct);

    /// <summary>
    /// Clients still in the book (PendingKyc / Active / Suspended) reachable at this
    /// phone number. A shared family or shop phone is legitimate, so a hit is only a
    /// WARNING the operator can override (<c>POSSIBLE_DUPLICATE_PHONE</c>).
    /// </summary>
    Task<IReadOnlyList<DuplicateHit>> FindActiveByPhoneAsync(
        Guid tenantId, string blindIndex, Guid? excludeClientId, CancellationToken ct);
}

internal sealed class DuplicateProbe(CustomersDbContext db) : IDuplicateProbe
{
    /// <summary>
    /// Statuses that still represent a real person in the book. Archived and Merged
    /// records must not raise a phone warning: the first is history, the second has
    /// already been folded into its survivor.
    /// </summary>
    /// <remarks>
    /// Typed as <see cref="List{T}"/> and NOT as an array on purpose. On an array,
    /// <c>Contains</c> now binds to the <c>ReadOnlySpan&lt;T&gt;</c> extension through the
    /// implicit array-to-span conversion, and the EF query translator chokes on the
    /// resulting <c>op_Implicit</c> node ("An exception was thrown while attempting to
    /// evaluate the LINQ query parameter expression"). <c>List&lt;T&gt;.Contains</c> is an
    /// instance method, so the predicate translates to a plain SQL <c>IN (...)</c>.
    /// </remarks>
    private static readonly List<ClientStatus> LiveStatuses =
    [
        ClientStatus.PendingKyc,
        ClientStatus.Active,
        ClientStatus.Suspended
    ];

    public async Task<DuplicateHit?> FindByIdentityDocumentAsync(
        Guid tenantId, string blindIndex, Guid? excludeClientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blindIndex)) return null;

        // IgnoreQueryFilters + explicit TenantId: this probe also runs from the lead
        // conversion path and from Hangfire jobs, where no HTTP tenant context exists.
        // The explicit predicate is what keeps tenants isolated there.
        var query = db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.IdentityDocumentNumberBlindIndex == blindIndex);

        if (excludeClientId.HasValue)
        {
            var excluded = excludeClientId.Value;
            query = query.Where(c => c.Id != excluded);
        }

        return await query
            .Select(c => new DuplicateHit(
                c.Id, c.ClientNumber, c.DisplayName, c.AgencyId,
                CustomerErrors.DuplicateIdentityDocument))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<DuplicateHit?> FindByRegistrationNumberAsync(
        Guid tenantId, string blindIndex, Guid? excludeClientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blindIndex)) return null;

        var query = db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && c.RegistrationNumberBlindIndex == blindIndex);

        if (excludeClientId.HasValue)
        {
            var excluded = excludeClientId.Value;
            query = query.Where(c => c.Id != excluded);
        }

        return await query
            .Select(c => new DuplicateHit(
                c.Id, c.ClientNumber, c.DisplayName, c.AgencyId,
                CustomerErrors.DuplicateRegistrationNumber))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<DuplicateHit>> FindActiveByPhoneAsync(
        Guid tenantId, string blindIndex, Guid? excludeClientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(blindIndex)) return [];

        // The phone lives on the child table, so this is a join rather than a column
        // comparison. Only OPEN contact points count (ValidTo is null): a closed one
        // is a number the client used to have.
        var query =
            from cp in db.ClientContactPoints.IgnoreQueryFilters()
            join c in db.Clients.IgnoreQueryFilters() on cp.ClientId equals c.Id
            where cp.TenantId == tenantId
               && c.TenantId == tenantId
               && cp.Type == ContactPointType.Phone
               && cp.ValidTo == null
               && cp.BlindIndex == blindIndex
               && LiveStatuses.Contains(c.Status)
            select c;

        if (excludeClientId.HasValue)
        {
            var excluded = excludeClientId.Value;
            query = query.Where(c => c.Id != excluded);
        }

        // Distinct because a client may legitimately hold the same number twice
        // (e.g. one labelled "personal" and one "shop") — the operator must see the
        // client once, not once per contact point.
        var hits = await query
            .Select(c => new DuplicateHit(
                c.Id, c.ClientNumber, c.DisplayName, c.AgencyId,
                CustomerErrors.PossibleDuplicatePhone))
            .Distinct()
            .ToListAsync(ct);

        return hits;
    }
}
