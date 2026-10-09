namespace Sankore.Modules.Integration.Features.Snapshot;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Criterion 2 of INT-21: the CBS's product codes become CRM product codes before anything is
/// stored.
///
/// <para>
/// <b>On the write side, deliberately.</b> Translating on read would mean every reader — the
/// facade, Customer 360, the KYC-ceiling job, whatever is written next — has to remember to do it,
/// and the one that forgets shows a CBS code to an agent who has never seen that catalogue. Doing
/// it once, here, makes the column itself be in CRM vocabulary, so a reader cannot get it wrong.
/// </para>
///
/// <para>
/// <b>An unmapped code is stored UNCHANGED.</b> <c>MappingResolver.ReverseAsync</c> answers null
/// for a code no mapping row covers, and this is a read model, not a command: dropping the code
/// would turn "a product we have not mapped yet" into "no product", which is unrecoverable from
/// the stored row and looks exactly like an account the CBS reported without one. Keeping the
/// foreign code is visibly odd on screen and tells the administrator precisely which mapping row
/// to add. The opposite trade-off is right for a WRITE — sending an unmapped code out creates a
/// record nobody can read, which is why <c>ResolveAsync</c> fails instead.
/// </para>
/// </summary>
internal sealed class SnapshotCodeTranslator(MappingResolver mappings)
{
    internal async Task<IReadOnlyList<CbsAccount>> TranslateAccountsAsync(
        Guid tenantId, Guid connectionId, IReadOnlyList<CbsAccount> accounts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        // One cache for accounts and loans would be nicer still, but the two lists are translated
        // in sequence and a customer's accounts commonly repeat one product code — eight accounts
        // on the same savings product would otherwise be eight identical queries per sync.
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var translated = new List<CbsAccount>(accounts.Count);

        foreach (var account in accounts)
        {
            translated.Add(account with
            {
                ProductCode = await ToCrmCodeAsync(tenantId, connectionId, account.ProductCode, cache, ct),

                // ProductLabel is NOT translated. It is the CBS's own wording, free text with no
                // mapping domain behind it, and inventing a CRM label here would put a string in
                // the column that no catalogue of ours actually contains.
            });
        }

        return translated;
    }

    internal async Task<IReadOnlyList<CbsLoan>> TranslateLoansAsync(
        Guid tenantId, Guid connectionId, IReadOnlyList<CbsLoan> loans, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(loans);

        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var translated = new List<CbsLoan>(loans.Count);

        foreach (var loan in loans)
        {
            translated.Add(loan with
            {
                ProductCode = await ToCrmCodeAsync(tenantId, connectionId, loan.ProductCode, cache, ct),
            });
        }

        return translated;
    }

    /// <summary>
    /// The CRM code for a CBS code, or the CBS code itself when no mapping row covers it.
    ///
    /// <para>
    /// The cache stores the null answers too: an unmapped code appearing on five accounts is one
    /// miss, not five. A null VALUE in the dictionary means "asked, and there is no mapping" —
    /// distinct from the key being absent, which is why <c>TryGetValue</c> and not an indexer with
    /// a null check.
    /// </para>
    /// </summary>
    private async Task<string?> ToCrmCodeAsync(
        Guid tenantId,
        Guid connectionId,
        string? cbsCode,
        Dictionary<string, string?> cache,
        CancellationToken ct)
    {
        // The CBS reported no product on this account. Nothing to translate, and inventing one
        // would be worse than the hole.
        if (string.IsNullOrWhiteSpace(cbsCode)) return cbsCode;

        if (!cache.TryGetValue(cbsCode, out var crmCode))
        {
            crmCode = await mappings.ReverseAsync(
                tenantId, connectionId, MappingDomain.Product, cbsCode, ct);

            cache[cbsCode] = crmCode;
        }

        return crmCode ?? cbsCode;
    }
}
