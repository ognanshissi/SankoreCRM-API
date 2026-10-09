namespace Sankore.Modules.Integration.Infrastructure.CallLog;

using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// What an adapter wraps every outbound operation in (INT-08, criterion 1).
///
/// <para>
/// It is a decorator and not a <c>WriteAsync</c> on purpose. An adapter that must remember to
/// append a row after each call will forget on the path it writes last — the timeout branch, the
/// early return when a mapping is missing — and those are exactly the calls a controller asks
/// about. Here the only way to perform the call is to hand it over, so the row is a consequence
/// of calling rather than a step the author has to add.
/// </para>
///
/// <para>
/// <b>Internal.</b> The journal is infrastructure of this module: the adapters live in their own
/// assemblies and see it through <c>InternalsVisibleTo</c>, and no other module has any business
/// writing into this table.
/// </para>
/// </summary>
internal interface ICallJournal
{
    /// <summary>
    /// Times <paramref name="operation"/>, records one row, and returns its result untouched.
    ///
    /// <para>
    /// The result is passed through verbatim — the journal never converts a failure into a
    /// success or the reverse. It is an observer with a side effect, and the side effect is
    /// allowed to fail silently; see <c>CallJournal</c> for why.
    /// </para>
    /// </summary>
    Task<IntegrationResult<T>> RecordAsync<T>(
        CallContext context, Func<CancellationToken, Task<IntegrationResult<T>>> operation, CancellationToken ct);

    /// <inheritdoc cref="RecordAsync{T}"/>
    Task<IntegrationResult> RecordAsync(
        CallContext context, Func<CancellationToken, Task<IntegrationResult>> operation, CancellationToken ct);
}

/// <summary>
/// Everything the journal knows before the call is made.
///
/// <para>
/// Built by the adapter and held by it for the duration of the call, which is how the
/// correlation id gets onto the wire: the adapter reads <see cref="Correlation"/> from the very
/// instance it passed in and sets it as <see cref="CallJournal.CorrelationHeader"/> on the
/// outbound request. One id, in our journal and in the back-office's own logs — the point M02
/// made on biometry: two deployments that share no database can still be joined on a support
/// ticket.
/// </para>
///
/// <para>
/// Note what is <b>not</b> here: no payload, no customer identifier, no account number. The
/// journal is handed to auditors (criterion 2) and a field it cannot receive is a field no
/// adapter can leak. <see cref="CommandId"/> and <see cref="ConnectionId"/> are this module's own
/// opaque keys and resolve to the rest only for someone already inside the tenant.
/// </para>
/// </summary>
/// <param name="Operation">
/// The logical operation, e.g. <c>CreateCustomer</c> — never the HTTP method. It is the grouping
/// key of the stats endpoint, so it must be stable across calls rather than descriptive of one.
/// </param>
/// <param name="CommandId">
/// The queued write this call serves, when there is one. Null for a read: a live balance lookup
/// and a health check belong in the journal too, and neither has a command behind it.
/// </param>
/// <param name="Endpoint">
/// Path, or a full URL — the entity's own <c>Sanitize</c> drops the query string and the fragment
/// either way, so an adapter that passes what it actually called cannot thereby store a customer
/// reference.
/// </param>
/// <param name="CorrelationId">
/// Supply one to join this call to an id that already exists upstream (a command's, a
/// verification's). Leave it null and one is generated — <see cref="Correlation"/> is never null,
/// because a row without a correlation id is a row support cannot follow.
/// </param>
internal sealed record CallContext(
    Guid TenantId,
    Guid ConnectionId,
    string Operation,
    Guid? CommandId = null,
    string? Endpoint = null,
    string? CorrelationId = null)
{
    /// <summary>
    /// The fallback, generated once per instance so that two reads of <see cref="Correlation"/>
    /// — the adapter's, for the header, and the journal's, for the row — cannot disagree.
    /// </summary>
    private readonly string _generatedCorrelationId = CallLogRedaction.NewCorrelationId();

    /// <summary>
    /// The id that is journalled and that the adapter must echo on the wire. Never null.
    ///
    /// <para>
    /// Derived rather than stored so a <c>with</c> expression stays correct: the compiler-generated
    /// copy constructor copies <see cref="_generatedCorrelationId"/> verbatim, so a clone that sets
    /// <see cref="CorrelationId"/> must take the new value from the property and not from the
    /// stale field.
    /// </para>
    /// </summary>
    internal string Correlation =>
        string.IsNullOrWhiteSpace(CorrelationId) ? _generatedCorrelationId : CorrelationId.Trim();

    /// <summary>
    /// Where the transport writes back the one fact it alone knows. Mutable, and deliberately so:
    /// the operation delegate returns an <c>IntegrationResult</c>, which carries a family and a
    /// code but no HTTP status, and the specification names status as a column of this journal.
    /// An adapter over HTTP sets it; a batch or relay adapter leaves it null, which is the honest
    /// answer for an exchange that had no HTTP status.
    /// </summary>
    internal CallProbe Probe { get; } = new();
}

/// <summary>
/// The transport's own observations about a call in flight. Deliberately tiny: anything larger
/// would become a place to stash a response body, which is the one thing this journal must never
/// hold.
/// </summary>
internal sealed class CallProbe
{
    /// <summary>Set by an HTTP adapter as soon as it has a response, success or not.</summary>
    internal int? HttpStatus { get; set; }
}
