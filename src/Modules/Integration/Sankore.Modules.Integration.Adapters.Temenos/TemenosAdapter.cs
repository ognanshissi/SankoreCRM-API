namespace Sankore.Modules.Integration.Adapters.Temenos;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Temenos Transact, over the Party and Holdings APIs (INT-12, INT-13).
///
/// <para>
/// <b>What it declares is what it does.</b> <see cref="Capabilities"/> lists the eight operations
/// implemented below and nothing else. <c>DebitAccount</c> and <c>ReverseDebit</c> are absent from
/// it on purpose: they belong to ASS-05 and are not in this chantier's criteria, so the two
/// methods exist (the port demands them) and answer
/// <see cref="IntegrationErrors.CapabilityNotSupported"/>. A screen reading the matrix therefore
/// hides the premium-debit button rather than offering one that fails, and
/// <c>IntegrationAdapterResolver.ResolvePort</c> refuses the call before it is ever made. Loans
/// and the insurance ports are not implemented at all — the adapter does not claim those
/// interfaces, which is a stronger statement than declaring them and refusing.
/// </para>
///
/// <para>
/// <b>Every outbound call goes through <c>ICallJournal</c></b> (INT-08, criterion 1), and the wrap
/// starts BEFORE the code translation rather than around the HTTP call alone. A command that
/// failed because a product mapping was missing is exactly the call a controller asks about, and
/// an adapter that journalled only what reached the network would leave that one as a gap — which
/// reads as an idle period, not as a failure.
/// </para>
///
/// <para>
/// <b>Which installation?</b> The ports take no connection, so the adapter resolves the tenant's
/// active core banking connection once per scope (<see cref="BindAsync"/>). It is scoped for that
/// reason: a singleton would carry one tenant's binding into the next tenant's job.
/// </para>
/// </summary>
internal sealed partial class TemenosAdapter(
    IntegrationDbContext db,
    ITenantContext tenant,
    TemenosTransport transport,
    TemenosCodeTranslation codes,
    ICallJournal journal,
    IOptions<TemenosAdapterOptions> options,
    TimeProvider clock,
    ILogger<TemenosAdapter> logger) :
    ICbsAdapter,
    ICbsCustomerPort,
    ICbsAccountPort,
    ICbsTransactionPort
{
    /// <summary>
    /// Resolved once per scope. Null until the first call: the constructor cannot query, and a
    /// connection read at construction would be read for every resolution of the adapter,
    /// including the ones that only look at <see cref="Capabilities"/>.
    /// </summary>
    private TemenosBinding? _binding;

    public IntegrationKind Kind => IntegrationKind.Temenos;

    /// <summary>
    /// The eight operations of INT-12 and INT-13, all live.
    ///
    /// <para>
    /// <see cref="CapabilityMode.RealTime"/> throughout, because Transact answers synchronously
    /// over HTTP: there is no file cycle in this adapter, and declaring batch would make the
    /// platform wait for an acknowledgement that nothing will ever produce.
    /// </para>
    /// </summary>
    public IntegrationCapabilities Capabilities { get; } = new(
        new Dictionary<IntegrationCapability, CapabilityMode>
        {
            // INT-12 — Party API.
            [IntegrationCapability.CreateCustomer] = CapabilityMode.RealTime,
            [IntegrationCapability.UpdateCustomer] = CapabilityMode.RealTime,
            [IntegrationCapability.SetKycLevel] = CapabilityMode.RealTime,

            // INT-13 — Holdings API.
            [IntegrationCapability.OpenAccount] = CapabilityMode.RealTime,
            [IntegrationCapability.ReadAccounts] = CapabilityMode.RealTime,
            [IntegrationCapability.ReadBalance] = CapabilityMode.RealTime,
            [IntegrationCapability.ReadTransactions] = CapabilityMode.RealTime,
            [IntegrationCapability.ReadMonthlyFlow] = CapabilityMode.RealTime,
        });

    // ── Health (INT-03) ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Authenticates and reads one bounded enquiry.
    ///
    /// <para>
    /// The token is minted afresh rather than taken from the cache
    /// (<c>forceRenew: true</c>): the question the activation screen is asking is "do these
    /// credentials work, right now", and answering it from a token minted ten minutes ago would
    /// let a connection be activated on credentials that have since been revoked.
    /// </para>
    ///
    /// <para>
    /// Takes the connection as an argument, unlike every other method here, because it is called
    /// BEFORE the connection is active — so there is nothing for <see cref="BindAsync"/> to find.
    /// </para>
    /// </summary>
    public async Task<IntegrationHealth> CheckHealthAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var startedAt = clock.GetUtcNow();

        if (connection.Settings is not TemenosSettings settings || string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return IntegrationHealth.Unhealthy(
                "The connection carries no usable Temenos settings (BaseUrl is required).", startedAt);
        }

        var binding = new TemenosBinding(connection, settings);

        var probe = await RunVoidAsync(
            binding,
            TemenosOperations.CheckHealth,
            TemenosPaths.Customers(binding.ApiVersion),
            async (ctx, callCt) =>
            {
                // The token is proved first and on purpose: a 401 on the enquiry would be reported
                // as "the installation refused us", which is true but hides that the credentials
                // are the part that is wrong.
                var token = await transport.ProveCredentialsAsync(binding, callCt);
                if (token.IsFailure) return IntegrationResultForwarding.Forward(token);

                var url = binding.Url(
                    TemenosPaths.Customers(binding.ApiVersion), $"{TemenosQuery.PageSize}=1");

                var answer = await transport.SendAsync<TemenosCustomer>(
                    binding, ctx, HttpMethod.Get, url, body: null, idempotencyKey: null, callCt);

                return answer.IsFailure ? IntegrationResultForwarding.Forward(answer) : IntegrationResult.Ok();
            },
            ct);

        var checkedAt = clock.GetUtcNow();
        var latency = checkedAt - startedAt;

        return probe.IsSuccess
            ? IntegrationHealth.Healthy(latency, checkedAt)
            : IntegrationHealth.Unhealthy($"{probe.Code}: {probe.Detail}", checkedAt, latency);
    }

    // ── Binding ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tenant's active core banking connection and its Temenos settings.
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate, as every background
    /// path in this module does. The predicate is not optional: this adapter is called from the
    /// Hangfire dispatcher, where the ambient tenant comes from
    /// <c>BackgroundJobContext.SetScope</c> and the global filter would be the only thing standing
    /// between a bug and a cross-tenant read. Both guards, so neither is load-bearing alone.
    /// </para>
    ///
    /// <para>
    /// The three failures are told apart because they send an administrator to three different
    /// screens: no tenant in context is a composition fault, no active connection is INT-03's
    /// configuration screen, and settings of the wrong shape is a row that must be corrected.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<TemenosBinding>> BindAsync(CancellationToken ct)
    {
        if (_binding is not null)
            return IntegrationResult.Ok(_binding);

        if (!tenant.HasTenant || tenant.CurrentTenantId == Guid.Empty)
        {
            return IntegrationResult.Technical<TemenosBinding>(
                IntegrationErrors.NoActiveConnection,
                "No tenant is established on this scope, so no Temenos connection can be resolved.");
        }

        var tenantId = tenant.CurrentTenantId;

        var connection = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                        && c.Kind == IntegrationKind.Temenos
                        && c.Family == IntegrationFamily.CoreBanking
                        && c.IsActive)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (connection is null)
        {
            return IntegrationResult.Technical<TemenosBinding>(
                IntegrationErrors.NoActiveConnection,
                "This tenant has no active Temenos core banking connection.");
        }

        if (connection.Settings is not TemenosSettings settings || string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            logger.LogError(
                "Connection {ConnectionId} is a Temenos row whose settings are unusable.",
                connection.Id);

            return IntegrationResult.Technical<TemenosBinding>(
                IntegrationErrors.SettingsInvalid,
                "The Temenos connection carries no usable settings (BaseUrl is required).");
        }

        _binding = new TemenosBinding(connection, settings);

        return IntegrationResult.Ok(_binding);
    }

    // ── Journalling ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds, then runs <paramref name="operation"/> inside one call-journal row.
    ///
    /// <para>
    /// One pair of methods for the whole adapter, so that no port method can be written without a
    /// row. The binding happens OUTSIDE the journal deliberately: a tenant with no configured
    /// connection made no call, and recording a row for it would fill the journal a controller
    /// reads with entries that never touched a back-office.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<T>> JournalledAsync<T>(
        string operationName,
        Func<TemenosBinding, string> endpoint,
        Func<TemenosBinding, CallContext, CancellationToken, Task<IntegrationResult<T>>> operation,
        CancellationToken ct)
    {
        var bound = await BindAsync(ct);
        if (bound.IsFailure)
            return IntegrationResultForwarding.Forward<T>(bound);

        var binding = bound.Value;

        return await RunAsync(
            binding, operationName, endpoint(binding),
            (ctx, callCt) => operation(binding, ctx, callCt), ct);
    }

    /// <summary>The same, for a write whose success carries nothing.</summary>
    private async Task<IntegrationResult> JournalledVoidAsync(
        string operationName,
        Func<TemenosBinding, string> endpoint,
        Func<TemenosBinding, CallContext, CancellationToken, Task<IntegrationResult>> operation,
        CancellationToken ct)
    {
        var bound = await BindAsync(ct);
        if (bound.IsFailure)
            return IntegrationResultForwarding.Forward(bound);

        var binding = bound.Value;

        return await RunVoidAsync(
            binding, operationName, endpoint(binding),
            (ctx, callCt) => operation(binding, ctx, callCt), ct);
    }

    /// <summary>
    /// The journal wrap alone, on a binding the caller already holds — which is the health
    /// check's situation, since it is called before the connection is active and therefore before
    /// <see cref="BindAsync"/> could find anything.
    /// </summary>
    private Task<IntegrationResult<T>> RunAsync<T>(
        TemenosBinding binding,
        string operationName,
        string endpoint,
        Func<CallContext, CancellationToken, Task<IntegrationResult<T>>> operation,
        CancellationToken ct)
    {
        var context = NewContext(binding, operationName, endpoint);

        return journal.RecordAsync(context, callCt => operation(context, callCt), ct);
    }

    /// <inheritdoc cref="RunAsync{T}"/>
    private Task<IntegrationResult> RunVoidAsync(
        TemenosBinding binding,
        string operationName,
        string endpoint,
        Func<CallContext, CancellationToken, Task<IntegrationResult>> operation,
        CancellationToken ct)
    {
        var context = NewContext(binding, operationName, endpoint);

        return journal.RecordAsync(context, callCt => operation(context, callCt), ct);
    }

    /// <summary>
    /// Builds the journal context. <c>CommandId</c> is always null here: a queued write's id is
    /// known to the dispatcher and not to the port, whose signature carries an idempotency key
    /// and no command. <c>Endpoint</c> is the path and never a full URL with a query string —
    /// the entity sanitises it either way, but a path keeps the row readable.
    /// </summary>
    private static CallContext NewContext(TemenosBinding binding, string operationName, string endpoint)
        => new(
            TenantId: binding.TenantId,
            ConnectionId: binding.ConnectionId,
            Operation: operationName,
            CommandId: null,
            Endpoint: endpoint);

    // ── ASS-05, not in this chantier ────────────────────────────────────────────────────────

    /// <summary>
    /// Not implemented, and declared as such.
    ///
    /// <para>
    /// The method exists because <c>ICbsAccountPort</c> demands it — the port carries the premium
    /// debit of ASS-05 — and this chantier's criteria (INT-13) do not include it. Answering
    /// <see cref="IntegrationErrors.CapabilityNotSupported"/> is the honest shape: the capability
    /// is absent from <see cref="Capabilities"/>, so the dispatcher refuses the command before
    /// reaching here and this body is the belt to that braces. A plausible implementation written
    /// without the criteria that govern it would be a method that moves money.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<CbsDebitReceipt>> DebitAccountAsync(
        ExternalId accountId, decimal amount, string currency, string label,
        IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(IntegrationResult.Technical<CbsDebitReceipt>(
            IntegrationErrors.CapabilityNotSupported,
            "The Temenos adapter does not implement DebitAccount (ASS-05 is not part of INT-13)."));

    /// <inheritdoc cref="DebitAccountAsync"/>
    public Task<IntegrationResult<CbsDebitReceipt>> ReverseDebitAsync(
        ExternalId accountId, string originalReference, IdempotencyKey key, CancellationToken ct)
        => Task.FromResult(IntegrationResult.Technical<CbsDebitReceipt>(
            IntegrationErrors.CapabilityNotSupported,
            "The Temenos adapter does not implement ReverseDebit (ASS-05 is not part of INT-13)."));
}

/// <summary>
/// The logical operation names the call journal groups on.
///
/// <para>
/// Constants and not literals at the call sites: <c>operation</c> is the grouping key of INT-08's
/// statistics endpoint, so one typo would split an operation into two series that nobody notices
/// are the same. They match the <c>IntegrationCapability</c> names, so a controller reading the
/// journal and an administrator reading the capability matrix are looking at the same word.
/// </para>
/// </summary>
internal static class TemenosOperations
{
    public const string CheckHealth = "CheckHealth";
    public const string CreateCustomer = "CreateCustomer";
    public const string SearchCustomer = "SearchCustomer";
    public const string UpdateCustomer = "UpdateCustomer";
    public const string SetKycLevel = "SetKycLevel";
    public const string OpenAccount = "OpenAccount";
    public const string ReadAccounts = "ReadAccounts";
    public const string ReadBalance = "ReadBalance";
    public const string ReadTransactions = "ReadTransactions";
    public const string ReadMonthlyFlow = "ReadMonthlyFlow";
}
