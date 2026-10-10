namespace Sankore.Modules.Integration.Adapters.Temenos;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// <c>ICbsAccountPort</c> on the Transact Holdings (arrangements) API (INT-13, criteria 1 and 2).
/// </summary>
internal sealed partial class TemenosAdapter
{
    /// <summary>
    /// Opens an arrangement for a party.
    ///
    /// <para>
    /// <b>The product code is translated, and an untranslatable one refuses the call</b> — INT-13's
    /// second criterion, literally. It is a <see cref="ErrorFamily.Technical"/> failure whose
    /// detail names the domain and the code, so an administrator reading the rejection queue knows
    /// which row of <c>integration_mapping</c> to add; <c>Functional</c> would send a clerk to
    /// re-enter a request that cannot succeed until a configuration changes, and
    /// <c>Transient</c> would retry a table that is not going to fill itself.
    /// </para>
    ///
    /// <para>
    /// The translation happens BEFORE the request is built and inside the journal row, so a
    /// missing mapping is a journalled failure and not a gap — the call a controller is most
    /// likely to ask about is the one that never left.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<ExternalId>> OpenAccountAsync(
        ExternalId customerId, string productCode, IdempotencyKey key, CancellationToken ct)
        => JournalledAsync<ExternalId>(
            TemenosOperations.OpenAccount,
            binding => TemenosPaths.Accounts(binding.ApiVersion),
            async (binding, ctx, callCt) =>
            {
                var external = await codes.RequiredAsync(
                    binding, MappingDomain.Product, productCode, callCt);

                if (external.IsFailure)
                    return IntegrationResultForwarding.Forward<ExternalId>(external);

                if (!customerId.HasValue)
                {
                    return IntegrationResultForwarding.Forward<ExternalId>(
                        MissingExternalId(TemenosOperations.OpenAccount));
                }

                var url = binding.Url(TemenosPaths.Accounts(binding.ApiVersion));

                var body = new TemenosAccountRequest
                {
                    CustomerId = customerId.Value,
                    ProductCode = external.Value,
                    Company = binding.Settings.CompanyId,
                };

                var opened = await transport.SendAsync<TemenosAccount>(
                    binding, ctx, HttpMethod.Post, url, body, key, callCt);

                if (opened.IsFailure)
                    return IntegrationResultForwarding.Forward<ExternalId>(opened);

                var accountId = opened.Value.First?.AccountId;

                // Same rule as a created party: an opening that does not name the account it
                // opened gives us nothing to store in integration_reference, and an empty
                // reference reads back later as a live link to nothing.
                return string.IsNullOrWhiteSpace(accountId)
                    ? IntegrationResultForwarding.Forward<ExternalId>(
                        TemenosErrorClassifier.Unexpected(
                            TemenosOperations.OpenAccount, "no accountId in the answer"))
                    : IntegrationResult.Ok(new ExternalId(accountId!.Trim()));
            },
            ct);

    /// <summary>
    /// Lists a party's accounts.
    ///
    /// <para>
    /// Product codes come back translated to the CRM's vocabulary where a mapping exists, and
    /// untranslated where none does — see <c>TemenosCodeTranslation.ReverseProductAsync</c>: an
    /// account the IMF opened before SANKORE existed carries a product the table has never heard
    /// of, and abandoning the whole list over it would empty Customer 360 for exactly the
    /// customers with the longest history.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<IReadOnlyList<CbsAccount>>> GetAccountsAsync(
        ExternalId customerId, CancellationToken ct)
        => JournalledAsync<IReadOnlyList<CbsAccount>>(
            TemenosOperations.ReadAccounts,
            binding => TemenosPaths.CustomerAccounts(binding.ApiVersion, customerId.Value),
            async (binding, ctx, callCt) =>
            {
                var accounts = await ReadAccountsAsync(binding, ctx, customerId, callCt);

                if (accounts.IsFailure)
                    return IntegrationResultForwarding.Forward<IReadOnlyList<CbsAccount>>(accounts);

                var mapped = new List<CbsAccount>(accounts.Value.Count);

                foreach (var wire in accounts.Value)
                {
                    var account = await ToDomainAsync(binding, wire, callCt);
                    if (account is not null) mapped.Add(account);
                }

                return IntegrationResult.Ok<IReadOnlyList<CbsAccount>>(mapped);
            },
            ct);

    /// <summary>
    /// Reads one account's balance.
    ///
    /// <para>
    /// <c>IsStale</c> stays <c>false</c>: this IS the live call, and the snapshot path that sets
    /// the flag belongs to INT-15's façade, which falls back to it when this call fails or the
    /// breaker is open. An adapter that set the flag itself would make every live figure look
    /// doubtful.
    /// </para>
    ///
    /// <para>
    /// <c>AsOf</c> is OUR read instant and not a field of the answer, because the Transact balance
    /// enquiry carries no timestamp. It is honest for a live read — the figure was true when the
    /// installation computed it, a few milliseconds before — and it is what the counter needs in
    /// order to tell this figure from a snapshot's.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<CbsBalance>> GetBalanceAsync(ExternalId accountId, CancellationToken ct)
        => JournalledAsync<CbsBalance>(
            TemenosOperations.ReadBalance,
            binding => TemenosPaths.AccountBalance(binding.ApiVersion, accountId.Value),
            async (binding, ctx, callCt) =>
            {
                if (!accountId.HasValue)
                {
                    return IntegrationResultForwarding.Forward<CbsBalance>(
                        MissingExternalId(TemenosOperations.ReadBalance));
                }

                var url = binding.Url(TemenosPaths.AccountBalance(binding.ApiVersion, accountId.Value));

                var answer = await transport.SendAsync<TemenosAccount>(
                    binding, ctx, HttpMethod.Get, url, body: null, idempotencyKey: null, callCt);

                if (answer.IsFailure)
                    return IntegrationResultForwarding.Forward<CbsBalance>(answer);

                var wire = answer.Value.First;
                var balance = TemenosWireFormats.ParseAmount(wire?.OnlineActualBalance);

                // A balance we cannot read is refused, never defaulted to zero: a zero shown at a
                // counter is indistinguishable from an empty account, and that is the one reading
                // a clerk acts on immediately.
                if (wire is null || balance is null || string.IsNullOrWhiteSpace(wire.Currency))
                {
                    return IntegrationResultForwarding.Forward<CbsBalance>(
                        TemenosErrorClassifier.Unexpected(
                            TemenosOperations.ReadBalance,
                            "the answer carries no readable balance and currency"));
                }

                return IntegrationResult.Ok(new CbsBalance(
                    AccountId: accountId,
                    Currency: wire.Currency!.Trim(),
                    Balance: balance.Value,
                    AvailableBalance: TemenosWireFormats.ParseAmount(wire.AvailableBalance)
                                      ?? TemenosWireFormats.ParseAmount(wire.WorkingBalance),
                    AsOf: clock.GetUtcNow(),
                    IsStale: false));
            },
            ct);

    // ── Shared reads ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The party's accounts as the wire reports them, inside the caller's journal row.
    ///
    /// <para>
    /// Shared with the monthly-flow aggregation, which needs the account list before it can walk
    /// any history. It does not open a row of its own, for the same reason the pre-create search
    /// does not: one logical operation is one row, or INT-08's statistics count a flow computation
    /// as several reads and the per-operation rates stop meaning anything.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<List<TemenosAccount>>> ReadAccountsAsync(
        TemenosBinding binding,
        CallContext ctx,
        ExternalId customerId,
        CancellationToken ct)
    {
        if (!customerId.HasValue)
        {
            return IntegrationResultForwarding.Forward<List<TemenosAccount>>(
                MissingExternalId(TemenosOperations.ReadAccounts));
        }

        var url = binding.Url(TemenosPaths.CustomerAccounts(binding.ApiVersion, customerId.Value));

        var answer = await transport.SendAsync<TemenosAccount>(
            binding, ctx, HttpMethod.Get, url, body: null, idempotencyKey: null, ct);

        return answer.IsFailure
            ? IntegrationResultForwarding.Forward<List<TemenosAccount>>(answer)
            : IntegrationResult.Ok(answer.Value.Body ?? []);
    }

    /// <summary>
    /// One wire account onto <c>CbsAccount</c>, or <c>null</c> when it carries no usable identity.
    ///
    /// <para>
    /// Skipped rather than refused: a single malformed row in a list of twelve accounts must not
    /// make a customer look like they have none. The row that cannot be addressed is the row that
    /// is dropped, which is the only outcome that cannot mislead — an account shown without a
    /// reference is one the counter cannot act on anyway.
    /// </para>
    /// </summary>
    private async Task<CbsAccount?> ToDomainAsync(
        TemenosBinding binding, TemenosAccount wire, CancellationToken ct)
    {
        var id = wire.AccountId;
        if (string.IsNullOrWhiteSpace(id)) return null;

        var balance = TemenosWireFormats.ParseAmount(wire.OnlineActualBalance) ?? 0m;

        return new CbsAccount(
            AccountId: new ExternalId(id!.Trim()),

            // accountReference is the externally printed number where an installation
            // distinguishes the two; accountId is both on the installations that do not.
            AccountNumber: (wire.AccountReference ?? id).Trim(),
            ProductCode: await codes.ReverseProductAsync(binding, wire.ProductCode, ct),
            ProductLabel: wire.ProductName,
            Currency: wire.Currency?.Trim() ?? string.Empty,
            Balance: balance,
            AvailableBalance: TemenosWireFormats.ParseAmount(wire.AvailableBalance)
                              ?? TemenosWireFormats.ParseAmount(wire.WorkingBalance),
            Status: wire.AccountStatus?.Trim() ?? string.Empty,
            OpenedOn: TemenosWireFormats.ParseDate(wire.OpeningDate));
    }
}
