namespace Sankore.Modules.Integration.Adapters.Temenos;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// <c>ICbsTransactionPort</c> on the Transact Holdings API (INT-13, criterion 3).
/// </summary>
internal sealed partial class TemenosAdapter
{
    /// <summary>
    /// One page of an account's history.
    ///
    /// <para>
    /// <b>The cursor is the installation's own <c>page_token</c>, passed through opaquely.</b> It
    /// is never parsed, never rebuilt from an offset, and never synthesised: the caller walks
    /// until <c>NextCursor</c> is null, and an empty token coming back is normalised to null so
    /// that "" cannot be fed back as a cursor and restart the walk from the top.
    /// </para>
    ///
    /// <para>
    /// A window with nothing in it is an empty page and a success, not a failure: an account with
    /// no movement in a month is an answer, and a refusal would make the monthly-flow aggregation
    /// abandon a customer who simply did not transact.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<CbsPage<CbsTransaction>>> GetTransactionsAsync(
        ExternalId accountId, DateOnly from, DateOnly to, string? cursor, CancellationToken ct)
        => JournalledAsync<CbsPage<CbsTransaction>>(
            TemenosOperations.ReadTransactions,
            binding => TemenosPaths.AccountTransactions(binding.ApiVersion, accountId.Value),
            async (binding, ctx, callCt) =>
            {
                if (!accountId.HasValue)
                {
                    return IntegrationResultForwarding.Forward<CbsPage<CbsTransaction>>(
                        MissingExternalId(TemenosOperations.ReadTransactions));
                }

                var page = await ReadTransactionPageAsync(binding, ctx, accountId, from, to, cursor, callCt);

                if (page.IsFailure)
                    return IntegrationResultForwarding.Forward<CbsPage<CbsTransaction>>(page);

                var items = page.Value.Items
                    .Select(ToDomain)
                    .Where(t => t is not null)
                    .Select(t => t!)
                    .ToList();

                return IntegrationResult.Ok(
                    new CbsPage<CbsTransaction>(items, page.Value.NextCursor));
            },
            ct);

    /// <summary>
    /// Money in and out over one calendar month, for the simplified-KYC ceiling of INT-22.
    ///
    /// <para>
    /// Transact exposes no monthly-flow enquiry, so the figure is computed: the party's accounts,
    /// then every movement of the month on each, paged to the end. That is the only way to answer
    /// it on this API, and it is why the aggregation is bounded — see
    /// <see cref="TemenosAdapterOptions.MaxPagesPerAggregation"/>.
    /// </para>
    ///
    /// <para>
    /// <b>One currency only, and that is deliberate.</b> The ceiling this feeds is a sum of money
    /// compared against a threshold expressed in one currency, so adding XOF to EUR would produce
    /// a number that is not an amount at all — and it would be wrong in the direction that lets a
    /// simplified file pass a ceiling it should have breached. The currency taken is that of the
    /// party's first account, which at a UEMOA institution is the customer's operating currency;
    /// accounts in another currency are excluded from the total rather than converted, because
    /// this adapter has no rate and inventing one would be worse than a partial figure.
    /// </para>
    ///
    /// <para>
    /// Credits and debits are returned separately and the caller's <c>Total</c> adds them:
    /// INT-22 measures the ceiling against everything that MOVED, not against the net. A customer
    /// who received and spent the same sum has used the account, and netting it to zero is how a
    /// simplified file passes a ceiling it should have breached.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<CbsMonthlyFlow>> GetMonthlyFlowAsync(
        ExternalId customerId, YearMonth month, CancellationToken ct)
        => JournalledAsync<CbsMonthlyFlow>(
            TemenosOperations.ReadMonthlyFlow,
            binding => TemenosPaths.CustomerAccounts(binding.ApiVersion, customerId.Value),
            async (binding, ctx, callCt) =>
            {
                var accounts = await ReadAccountsAsync(binding, ctx, customerId, callCt);
                if (accounts.IsFailure)
                    return IntegrationResultForwarding.Forward<CbsMonthlyFlow>(accounts);

                var currency = accounts.Value
                    .Select(a => a.Currency?.Trim())
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

                // No account at all is a flow of zero and a success: a party exists, it simply
                // holds nothing. Refusing would make the KYC ceiling unanswerable for a customer
                // whose file is being built before their first account is opened.
                if (currency is null)
                    return IntegrationResult.Ok(new CbsMonthlyFlow(month, 0m, 0m, string.Empty));

                var credit = 0m;
                var debit = 0m;

                foreach (var account in accounts.Value)
                {
                    if (string.IsNullOrWhiteSpace(account.AccountId)) continue;

                    // See the remarks: excluded, not converted.
                    if (!string.Equals(account.Currency?.Trim(), currency, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation(
                            "Excluding an account in {Other} from the monthly flow computed in "
                            + "{Currency} | Connection={ConnectionId} Correlation={Correlation}",
                            account.Currency, currency, binding.ConnectionId, ctx.Correlation);

                        continue;
                    }

                    var walked = await SumAccountAsync(
                        binding, ctx, new ExternalId(account.AccountId!.Trim()), month, callCt);

                    if (walked.IsFailure)
                        return IntegrationResultForwarding.Forward<CbsMonthlyFlow>(walked);

                    credit += walked.Value.Credit;
                    debit += walked.Value.Debit;
                }

                return IntegrationResult.Ok(new CbsMonthlyFlow(month, credit, debit, currency));
            },
            ct);

    /// <summary>
    /// Walks one account's whole month and sums each direction.
    ///
    /// <para>
    /// The page bound and the "cursor did not change" check are both needed, and they catch
    /// different faults: the first stops a month that genuinely does not fit (or a pager that
    /// always issues a fresh token), the second catches the commoner bug of an installation that
    /// echoes the token it was given. Either one, unguarded, is a Hangfire job that spins for ever
    /// with nothing in the logs. Both are reported as
    /// <see cref="IntegrationErrors.UnexpectedResponse"/>, because a pager that does not advance
    /// is exactly a body that does not match the documented contract.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<FlowTotals>> SumAccountAsync(
        TemenosBinding binding,
        CallContext ctx,
        ExternalId accountId,
        YearMonth month,
        CancellationToken ct)
    {
        var credit = 0m;
        var debit = 0m;
        string? cursor = null;
        var pages = 0;
        var maxPages = options.Value.MaxPagesPerAggregation;

        do
        {
            var page = await ReadTransactionPageAsync(
                binding, ctx, accountId, month.FirstDay, month.LastDay, cursor, ct);

            if (page.IsFailure)
                return IntegrationResultForwarding.Forward<FlowTotals>(page);

            foreach (var movement in page.Value.Items)
            {
                var amount = TemenosWireFormats.ParseAmount(movement.Amount);
                if (amount is null) continue;

                if (IsDebit(movement)) debit += Math.Abs(amount.Value);
                else credit += Math.Abs(amount.Value);
            }

            var next = page.Value.NextCursor;

            if (next is not null && next == cursor)
            {
                return IntegrationResultForwarding.Forward<FlowTotals>(
                    TemenosErrorClassifier.Unexpected(
                        TemenosOperations.ReadMonthlyFlow,
                        "the page token did not advance, so the history cannot be walked"));
            }

            cursor = next;

            if (++pages > maxPages)
            {
                return IntegrationResultForwarding.Forward<FlowTotals>(
                    TemenosErrorClassifier.Unexpected(
                        TemenosOperations.ReadMonthlyFlow,
                        $"more than {maxPages} pages for one month on one account"));
            }
        }
        while (cursor is not null);

        return IntegrationResult.Ok(new FlowTotals(credit, debit));
    }

    /// <summary>
    /// One raw page, shared by the port method and by the aggregation, inside the caller's
    /// journal row.
    /// </summary>
    private async Task<IntegrationResult<WirePage>> ReadTransactionPageAsync(
        TemenosBinding binding,
        CallContext ctx,
        ExternalId accountId,
        DateOnly from,
        DateOnly to,
        string? cursor,
        CancellationToken ct)
    {
        var query = new List<string>
        {
            $"{TemenosQuery.FromDate}={TemenosWireFormats.Format(from)}",
            $"{TemenosQuery.ToDate}={TemenosWireFormats.Format(to)}",
            $"{TemenosQuery.PageSize}={options.Value.TransactionPageSize}",
        };

        // Escaped, because the token is opaque and an installation is free to put a slash, a plus
        // or a colon in it — and an unescaped plus is read back as a space, which is how a pager
        // silently restarts from the first page.
        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add($"{TemenosQuery.PageToken}={Uri.EscapeDataString(cursor!)}");

        var url = binding.Url(
            TemenosPaths.AccountTransactions(binding.ApiVersion, accountId.Value),
            string.Join('&', query));

        var answer = await transport.SendAsync<TemenosTransaction>(
            binding, ctx, HttpMethod.Get, url, body: null, idempotencyKey: null, ct);

        if (answer.IsFailure)
            return IntegrationResultForwarding.Forward<WirePage>(answer);

        var token = answer.Value.Header?.PageToken;

        return IntegrationResult.Ok(new WirePage(
            answer.Value.Body ?? [],
            // Normalised: an empty token means "last page", and handing "" back as a cursor
            // would have the caller ask for the first page again, for ever.
            string.IsNullOrWhiteSpace(token) ? null : token!.Trim()));
    }

    /// <summary>
    /// One movement onto <c>CbsTransaction</c>, or <c>null</c> when it has no reference.
    ///
    /// <para>
    /// The reference is not decoration: the contract requires a page boundary never to re-serve a
    /// movement, and a movement with no reference cannot be told from another one. Dropping it is
    /// safer than counting it twice in a figure a regulator measures a ceiling against.
    /// </para>
    /// </summary>
    private static CbsTransaction? ToDomain(TemenosTransaction wire)
    {
        if (string.IsNullOrWhiteSpace(wire.TransactionReference)) return null;

        var amount = TemenosWireFormats.ParseAmount(wire.Amount);
        if (amount is null) return null;

        var valueDate = TemenosWireFormats.ParseDate(wire.ValueDate)
                        ?? TemenosWireFormats.ParseDate(wire.BookingDate);

        if (valueDate is null) return null;

        var booked = TemenosWireFormats.ParseDate(wire.BookingDate);

        return new CbsTransaction(
            Reference: wire.TransactionReference!.Trim(),
            ValueDate: valueDate.Value,

            // A date and not a timestamp is what the enquiry gives; midnight UTC is the honest
            // reading of "this day", and the caller has ValueDate for the business date anyway.
            BookedAt: booked is { } b ? new DateTimeOffset(b.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,

            // Absolute, with the sign carried by Direction — the same shape the in-memory double
            // uses, so a report built on one does not change meaning on the other.
            Amount: Math.Abs(amount.Value),
            Currency: wire.Currency?.Trim() ?? string.Empty,
            Direction: IsDebit(wire) ? TemenosDirections.Debit : TemenosDirections.Credit,
            Label: wire.Narrative,
            CounterpartyLabel: wire.CounterpartyName);
    }

    /// <summary>
    /// Indicator first, sign second. See <c>TemenosTransaction</c>: installations differ on which
    /// of the two they use, and reading only the sign on one that uses the indicator would file
    /// every debit as a credit.
    /// </summary>
    private static bool IsDebit(TemenosTransaction wire)
    {
        var indicator = wire.DebitCreditIndicator?.Trim();

        if (!string.IsNullOrEmpty(indicator))
        {
            if (indicator.StartsWith(TemenosDirections.WireDebit, StringComparison.OrdinalIgnoreCase))
                return true;

            if (indicator.StartsWith(TemenosDirections.WireCredit, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return TemenosWireFormats.ParseAmount(wire.Amount) is < 0m;
    }

    /// <summary>A page as the wire gave it, before domain mapping.</summary>
    private sealed record WirePage(List<TemenosTransaction> Items, string? NextCursor);

    /// <summary>The two halves INT-22 adds together.</summary>
    private readonly record struct FlowTotals(decimal Credit, decimal Debit);
}
