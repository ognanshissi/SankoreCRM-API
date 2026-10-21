namespace Sankore.Modules.Integration;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Commands;
using Sankore.Modules.Integration.Features.References.GetReference;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Implementation of <see cref="IIntegrationModule"/>. Callers in other modules see only the
/// interface — and never an adapter, because the IMF's CBS is chosen by the IMF.
///
/// <para>
/// <b>Every read bypasses the tenant query filter and re-applies the tenant explicitly</b>, the
/// same choice <c>KycModuleFacade</c> documents and for the same reason: the callers of this
/// contract run outside any HTTP request — M01's Hangfire jobs, M02's consumers, this module's
/// own dispatcher — so the ambient <see cref="ITenantContext"/> cannot be trusted to be the
/// tenant being operated on. The tenant comes from <see cref="ICurrentUser"/>, which
/// <c>BackgroundJobContext.SetScope</c> fills for a job exactly as the JWT fills it for a
/// request.
/// </para>
/// </summary>
public sealed class IntegrationModuleFacade : IIntegrationModule
{
    /// <summary>
    /// How the snapshot's jsonb columns are read.
    ///
    /// <para>
    /// Case-insensitive on purpose: the writer is the synchronisation of INT-21, in another
    /// chantier, and a read model that becomes unreadable because one side chose camelCase and
    /// the other PascalCase is a silent empty Customer 360 — the figures would simply all be
    /// zero, with nothing in the logs.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IntegrationDbContext _db;
    private readonly IntegrationAdapterResolver _resolver;
    private readonly ReferenceLookup _references;
    private readonly CommandPayloadProtector _protector;
    private readonly CbsCustomerPayloadSource _payloads;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntegrationModuleFacade> _logger;

    /// <summary>
    /// INT-15's cache and circuit-breaker reading, used by one method
    /// (<see cref="CoreBankingGateway.GetLiveBalanceAsync"/>).
    ///
    /// <para>
    /// Nullable, and the only collaborator here that is: it carries an optimisation, so a
    /// container without it must degrade to a direct CBS call rather than fail every caller of
    /// this facade — including the write paths, which do not use it at all.
    /// </para>
    /// </summary>
    private readonly Features.Balance.LiveBalanceGate? _balanceGate;

    /// <summary>
    /// For the one gateway member that delegates to a query rather than re-projecting it —
    /// <c>GetCallContextAsync</c>, so the perimeter rule of TEL-B-10 has one definition.
    /// Nullable because this module's own tests construct the facade directly.
    /// </summary>
    private readonly MediatR.ISender? _sender;

    /// <summary>
    /// The WhatsApp numbers' directory (WA-BE-02). Optional like the two above, for the same
    /// reason: this module's own tests build the facade directly and never send a message. The
    /// messaging gateway throws without it rather than answering "no channel", which would read as
    /// a configuration problem instead of a wiring one.
    /// </summary>
    private readonly Features.Messaging.MessagingChannels? _messaging;

    /// <summary>
    /// The constructor the container uses.
    ///
    /// <para>
    /// It takes the provider and not the collaborators because of an accessibility rule rather
    /// than a design preference: this type is PUBLIC (it implements the module's public contract)
    /// while every collaborator it needs is <c>internal</c> — the adapter resolver, the payload
    /// protector, the reference lookup — and a public constructor cannot name an internal type.
    /// Making them public to satisfy the compiler would export the module's internals to every
    /// consumer, which is the one thing the PublicApi split exists to prevent.
    /// </para>
    ///
    /// <para>
    /// The facade is registered Scoped, so the provider handed in is the request's (or the job's)
    /// own scope and these resolve to exactly the same DbContext instance the caller is using —
    /// which is what makes <see cref="EnqueueAsync"/> able to enlist in the caller's transaction.
    /// </para>
    /// </summary>
    public IntegrationModuleFacade(IServiceProvider services)
        : this(
            services.GetRequiredService<IntegrationDbContext>(),
            services.GetRequiredService<IntegrationAdapterResolver>(),
            services.GetRequiredService<ReferenceLookup>(),
            services.GetRequiredService<CommandPayloadProtector>(),
            services.GetRequiredService<CbsCustomerPayloadSource>(),
            services.GetRequiredService<ICurrentUser>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<IntegrationModuleFacade>>(),
            // GetService, not GetRequiredService: see the field. A deployment whose container has
            // no Redis, or a host that has not called AddBalanceServices, still gets live
            // balances — straight from the CBS, uncached.
            services.GetService<Features.Balance.LiveBalanceGate>(),
            // GetService for the same reason as the balance gate: this module's own tests build
            // the facade directly and almost none of them dispatch a query. The one gateway
            // member that needs it throws rather than answering null — a context read that
            // silently said "nobody is on this call" would be read as a resolution failure.
            services.GetService<MediatR.ISender>(),
            services.GetService<Features.Messaging.MessagingChannels>())
    {
    }

    /// <summary>Explicit dependencies, for this module's own tests.</summary>
    internal IntegrationModuleFacade(
        IntegrationDbContext db,
        IntegrationAdapterResolver resolver,
        ReferenceLookup references,
        CommandPayloadProtector protector,
        CbsCustomerPayloadSource payloads,
        ICurrentUser currentUser,
        TimeProvider clock,
        ILogger<IntegrationModuleFacade> logger,
        Features.Balance.LiveBalanceGate? balanceGate = null,
        MediatR.ISender? sender = null,
        Features.Messaging.MessagingChannels? messaging = null)
    {
        _db = db;
        _resolver = resolver;
        _references = references;
        _protector = protector;
        _payloads = payloads;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
        _balanceGate = balanceGate;
        _sender = sender;
        _messaging = messaging;

        CoreBanking = new CoreBankingGateway(this);
        Insurance = new InsuranceGateway(this);
        Telephony = new TelephonyGateway(this);
        Messaging = new MessagingGateway(this);
    }

    public ICoreBankingGateway CoreBanking { get; }

    public IInsuranceGateway Insurance { get; }

    public ITelephonyGateway Telephony { get; }

    public IMessagingGateway Messaging { get; }

    // ── The heart of INT-05: enqueueing inside the caller's transaction ─────

    /// <summary>
    /// Creates the command row, or hands back the one that already exists.
    ///
    /// <para>
    /// <b>It does not call <c>SaveChangesAsync</c>, and that is the single most important line of
    /// this file.</b> The row is added to the DbContext the caller's own unit of work is already
    /// using, so it is committed by the caller's <c>SaveChangesAsync</c> — inside the ambient
    /// transaction <c>TransactionBehavior</c> opened — and by nothing else.
    /// </para>
    ///
    /// <para>
    /// Saving here would commit a write order whose business reason may still roll back. A client
    /// validation that fails its last rule after this point would leave a command queued to
    /// create a customer in the core banking system that SANKORE never created: a row in a
    /// production CBS that no SANKORE record corresponds to, produced by a transaction that
    /// failed. The symmetrical risk — the caller commits and the command is lost — cannot happen,
    /// because there is only one transaction and the command is in it.
    /// </para>
    ///
    /// <para>
    /// The price is that the caller MUST save. That is the normal contract of every handler in
    /// this repo (the outbox publisher works exactly this way and says so), and a caller that
    /// forgets simply queues nothing, which its own test catches.
    /// </para>
    /// </summary>
    private async Task<IntegrationCommandId> EnqueueAsync(
        Guid tenantId,
        IntegrationConnection connection,
        CommandType commandType,
        string entityType,
        Guid crmId,
        IdempotencyKey key,
        ProtectedPayload? payload,
        CancellationToken ct)
    {
        // Two lookups, not one, and both are needed.
        //
        // The change tracker first: two identical requests inside ONE unit of work have not
        // reached the database yet, so the query below cannot see the first one. That is not a
        // hypothetical — a handler that validates a client and opens an account for it calls two
        // gateways in the same transaction.
        var pending = _db.ChangeTracker
            .Entries<IntegrationCommand>()
            .FirstOrDefault(e => e.State == EntityState.Added
                                 && e.Entity.TenantId == tenantId
                                 && e.Entity.IdempotencyKey == key.Value);

        if (pending is not null)
            return new IntegrationCommandId(pending.Entity.Id);

        // Then the table, which is where a request repeated minutes or days later collides. The
        // unique index ux_integration_command_tenant_idempotency is the guarantee; this read is
        // what turns it into "here is the command you already asked for" instead of an exception
        // in the caller's transaction.
        var existing = await _db.Commands
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.IdempotencyKey == key.Value)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is { } existingId)
        {
            _logger.LogDebug(
                "{CommandType} for {EntityType} {CrmId} is already queued as {CommandId}",
                commandType, entityType, crmId, existingId);

            return new IntegrationCommandId(existingId);
        }

        var command = IntegrationCommand.Create(
            tenantId: tenantId,
            connectionId: connection.Id,
            commandType: commandType,
            entityType: entityType,
            crmId: crmId,
            idempotencyKey: key,
            createdBy: _currentUser.Id,
            clock: _clock,
            payloadEncrypted: payload?.Ciphertext,
            payloadFieldNames: payload?.FieldNames);

        _db.Commands.Add(command);

        return new IntegrationCommandId(command.Id);
    }

    /// <summary>
    /// The tenant's active core-banking connection, or a <see cref="DomainException"/>.
    ///
    /// <para>
    /// Throwing is the only channel these three methods have — they return an
    /// <see cref="IntegrationCommandId"/>, never a result, because the write has not happened
    /// yet. It is also the right one: "this tenant has no CBS configured" is not an outcome the
    /// calling business rule can do anything about, and <c>DomainExceptionHandler</c> already
    /// turns a <c>DomainException</c> into a 422 carrying the code.
    /// </para>
    /// </summary>
    private async Task<IntegrationConnection> RequireCoreBankingAsync(Guid tenantId, CancellationToken ct)
    {
        var connection = await _resolver.ResolveConnectionAsync(
            tenantId, IntegrationFamily.CoreBanking, ct);

        return connection.IsSuccess
            ? connection.Value
            : throw new DomainException($"{IntegrationErrors.NoActiveConnection}: {connection.Detail}");
    }

    private Guid TenantId => _currentUser.TenantId;

    // ── Core banking ────────────────────────────────────────────────────────

    /// <summary>
    /// Core banking, as the rest of the platform sees it.
    ///
    /// <para>
    /// Nested and <c>internal sealed</c>: it is an implementation detail of the facade, holds no
    /// state of its own, and exists only so one consumer-facing interface can be split in two
    /// without two registrations that could disagree about the tenant.
    /// </para>
    /// </summary>
    internal sealed class CoreBankingGateway(IntegrationModuleFacade facade) : ICoreBankingGateway
    {
        public async Task<IntegrationCommandId> RequestCustomerCreationAsync(
            Guid crmCustomerId, CancellationToken ct)
        {
            var tenantId = facade.TenantId;
            var connection = await facade.RequireCoreBankingAsync(tenantId, ct);

            // Assembled now and stored encrypted: the write owed is the write as it was decided.
            // A null payload means M01 does not know this id in this tenant — refused rather than
            // queued, because a creation command for a customer that does not exist can only
            // ever be rejected, eight attempts later.
            var payload = await facade._payloads.BuildAsync(tenantId, crmCustomerId, ct)
                ?? throw new DomainException(
                    $"{IntegrationErrors.ExternalEntityNotFound}: customer {crmCustomerId} is "
                    + "unknown to the clients module in this tenant.");

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.CreateCustomer,
                IntegrationEntityTypes.Customer,
                crmCustomerId,
                IdempotencyKeyFactory.ForCustomerCreation(tenantId, connection.Id, crmCustomerId),
                facade._protector.Protect(payload),
                ct);
        }

        public async Task<IntegrationCommandId> RequestKycLevelUpdateAsync(
            Guid crmCustomerId, KycLevel level, CancellationToken ct)
        {
            var tenantId = facade.TenantId;
            var connection = await facade.RequireCoreBankingAsync(tenantId, ct);

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.SetKycLevel,
                IntegrationEntityTypes.Customer,
                crmCustomerId,
                IdempotencyKeyFactory.ForKycLevelUpdate(tenantId, connection.Id, crmCustomerId, level),
                facade._protector.Protect(new SetKycLevelPayload(level)),
                ct);
        }

        public async Task<IntegrationCommandId> RequestAccountOpeningAsync(
            Guid crmCustomerId, string productCode, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(productCode);

            var tenantId = facade.TenantId;
            var connection = await facade.RequireCoreBankingAsync(tenantId, ct);

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.OpenAccount,
                // The ACCOUNT is what gets created, so the reference the dispatcher writes on
                // success is an account reference. No module of this platform owns an account
                // record, so it is keyed by the customer it belongs to — see the handler.
                IntegrationEntityTypes.Account,
                crmCustomerId,
                IdempotencyKeyFactory.ForAccountOpening(
                    tenantId, connection.Id, crmCustomerId, productCode),
                facade._protector.Protect(new OpenAccountPayload(productCode.Trim())),
                ct);
        }

        /// <summary>
        /// The premium debit of ASS-05's first step. The connection is the tenant's core banking
        /// one — a premium is collected from the customer's own account, whichever insurer the
        /// policy is with.
        /// </summary>
        public async Task<IntegrationCommandId> RequestPremiumDebitAsync(
            PremiumDebitRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.AccountRef);

            if (request.Amount <= 0m)
            {
                throw new DomainException(
                    $"{IntegrationErrors.PayloadInvalid}: a premium debit needs a positive amount.");
            }

            var tenantId = facade.TenantId;
            var connection = await facade.RequireCoreBankingAsync(tenantId, ct);

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.DebitAccount,
                // A debit creates no addressable entity — AcknowledgementApplier maps it to no
                // reference at all — so the entity type is the account it acts on and the CRM id
                // is the SUBSCRIPTION, which is what makes this debit unique and what lets a
                // command sitting in the rejection queue say what it was for.
                IntegrationEntityTypes.Account,
                request.SubscriptionId,
                IdempotencyKeyFactory.ForPremiumDebit(
                    tenantId, connection.Id, request.SubscriptionId),
                facade._protector.Protect(new DebitAccountPayload(
                    request.AccountRef.Trim(),
                    request.Amount,
                    request.Currency.Trim().ToUpperInvariant(),
                    request.Label)),
                ct);
        }

        /// <summary>
        /// One attempt at one premium instalment. The attempt is in the key — see the contract.
        /// </summary>
        public async Task<IntegrationCommandId> RequestInstalmentDebitAsync(
            InstalmentDebitRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.AccountRef);

            if (request.Amount <= 0m)
            {
                throw new DomainException(
                    $"{IntegrationErrors.PayloadInvalid}: an instalment debit needs a positive amount.");
            }

            var tenantId = facade.TenantId;
            var connection = await facade.RequireCoreBankingAsync(tenantId, ct);

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.DebitAccount,
                IntegrationEntityTypes.Account,
                // The INSTALMENT is the CRM id, so a command in the rejection queue says which
                // instalment of which policy it was for.
                request.InstalmentId,
                IdempotencyKeyFactory.ForInstalmentDebit(
                    tenantId, connection.Id, request.InstalmentId, request.Attempt),
                facade._protector.Protect(new DebitAccountPayload(
                    request.AccountRef.Trim(),
                    request.Amount,
                    request.Currency.Trim().ToUpperInvariant(),
                    request.Label)),
                ct);
        }

        public async Task<IntegrationCommandId> RequestDebitReversalAsync(
            DebitReversalRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.AccountRef);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.OriginalReference);

            var tenantId = facade.TenantId;
            var connection = await facade.RequireCoreBankingAsync(tenantId, ct);

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.ReverseDebit,
                IntegrationEntityTypes.Account,
                request.SubscriptionId,
                // The ATTEMPT is in the key. See the contract: without it an operator's second
                // attempt after a refused reversal is swallowed as a replay, and the customer
                // stays un-refunded while the platform reports the refund as requested.
                IdempotencyKeyFactory.ForDebitReversal(
                    tenantId, connection.Id, request.SubscriptionId, request.Attempt),
                facade._protector.Protect(new ReverseDebitPayload(
                    request.AccountRef.Trim(), request.OriginalReference.Trim())),
                ct);
        }

        /// <summary>
        /// The read model Customer 360 shows without touching the CBS (INT-21). <c>null</c> when
        /// the synchronisation has never run for this customer — an ordinary state, and the
        /// screen says "not synchronised" rather than showing zeros.
        /// </summary>
        public async Task<CbsCustomerSnapshot?> GetCustomerSnapshotAsync(
            Guid crmCustomerId, CancellationToken ct)
        {
            var snapshot = await facade.ReadSnapshotAsync(facade.TenantId, crmCustomerId, ct);
            if (snapshot is null) return null;

            return new CbsCustomerSnapshot(
                CrmCustomerId: snapshot.CrmCustomerId,
                Accounts: Deserialise<CbsAccount>(snapshot.AccountsJson),
                Loans: Deserialise<CbsLoan>(snapshot.LoansJson),
                TotalBalance: snapshot.TotalBalance,
                MonthlyFlow: snapshot.MonthlyFlow,
                KycLevelInCbs: snapshot.KycLevelInCbs,
                SnapshotAt: snapshot.SnapshotAt);
        }

        /// <summary>
        /// A balance, live when the installation can answer live, from the snapshot otherwise —
        /// and <c>IsStale</c> tells the counter clerk which of the two they are looking at.
        ///
        /// <para>
        /// INT-15 adds two things around that straight call, both in
        /// <see cref="Features.Balance.LiveBalanceGate"/>: a 60-second distributed cache per
        /// account, and a short-circuit when the connection's breaker is open. Neither may change
        /// the answer — a cached figure is a live figure that was true a moment ago, and an open
        /// breaker produces exactly the fallback a failed call produces.
        /// </para>
        /// </summary>
        public async Task<CbsBalance?> GetLiveBalanceAsync(
            Guid crmCustomerId, string accountRef, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(accountRef)) return null;

            var tenantId = facade.TenantId;

            var resolved = await facade._resolver.ResolveAsync(
                tenantId, IntegrationFamily.CoreBanking, ct);

            // No connection, or no adapter for its kind: nothing can be asked, so the snapshot is
            // the only answer there is — marked stale.
            if (resolved.IsFailure)
                return await facade.StaleBalanceAsync(tenantId, crmCustomerId, accountRef, ct);

            // The PAIR, not the bare adapter: the capability read below and the port resolution
            // are both questions about this connection's installation, and unwrapping here is
            // exactly how the connection used to get dropped.
            var adapter = resolved.Value;
            var connectionId = adapter.Connection.Id;

            // THE ACCOUNT MUST BELONG TO THE CUSTOMER, and that is checked before anything is
            // asked of the CBS. INT-15 restricts this read to the clients of the agent's own
            // agency, and the perimeter is enforced on the CUSTOMER — so an unbound accountRef
            // would let a caller pass any customer they legitimately see plus ANY account
            // reference in the core banking system: other branches' customers, and other tenants
            // wherever the CBS numbering is sequential. Reading arbitrary balances is precisely
            // what the perimeter compartmentalises.
            var ownedAccount = await facade.ResolveOwnedAccountAsync(
                tenantId, connectionId, crmCustomerId, accountRef, ct);

            // Neither we nor the snapshot know this account as the customer's. No call is made —
            // the absence of the call IS the control — and the answer is null rather than an
            // error: it matches the method's own contract (an account the CBS never heard of is
            // an ordinary state) and says nothing about whether it exists somewhere else.
            if (ownedAccount is null) return null;

            // INT-15's 60-second cache, read HERE and never earlier: the ownership check above is
            // a security control, and a cache hit that preceded it would be a way around it — the
            // caller would be served a figure for an account it was never shown to own. Keyed on
            // the id WE resolved, so the key holds no caller-supplied text and two spellings of
            // one account share one entry.
            if (facade._balanceGate is { } gate)
            {
                var cached = await gate.TryReadAsync(tenantId, connectionId, ownedAccount.Value, ct);
                if (cached is not null) return cached;
            }

            // A capability served in Batch mode is not a slow live call: there is no live call at
            // all, and the figure can only come from the last file that was loaded.
            if (!adapter.Capabilities.IsRealTime(IntegrationCapability.ReadBalance))
                return await facade.StaleBalanceAsync(tenantId, crmCustomerId, accountRef, ct);

            // The breaker is open: INT-09 has already cut this connection off after N consecutive
            // failures, so the call would fail and fall back anyway. Checked rather than attempted
            // because Polly's half-open trial call is a scarce thing — spending it on a screen
            // refresh delays the recovery of the write path, which has no fallback at all.
            if (facade._balanceGate?.IsCircuitOpen(connectionId) == true)
            {
                facade._logger.LogWarning(
                    "Live balance for account {AccountRef} skipped: the circuit of connection "
                    + "{ConnectionId} is open; falling back to the snapshot",
                    accountRef, connectionId);

                return await facade.StaleBalanceAsync(tenantId, crmCustomerId, accountRef, ct);
            }

            var port = facade._resolver.ResolvePort<ICbsAccountPort>(
                adapter, IntegrationCapability.ReadBalance);

            if (port.IsFailure)
                return await facade.StaleBalanceAsync(tenantId, crmCustomerId, accountRef, ct);

            // Called with OUR matched id, never with the caller's string. A formatted variant can
            // match one of our rows loosely and still resolve to something else at the CBS, and
            // that normalisation gap is the whole value of passing the matched record's id.
            var balance = await port.Value.GetBalanceAsync(ownedAccount.Value, ct);

            if (balance.IsSuccess)
            {
                // Only a live success is remembered. Caching the snapshot fallback instead would
                // make one CBS outage freeze the figure for a further 60 seconds after the CBS came
                // back — the cache answering for a system that is answering again.
                if (facade._balanceGate is { } writable)
                    await writable.WriteAsync(
                        tenantId, connectionId, ownedAccount.Value, balance.Value, ct);

                return balance.Value;
            }

            // The CBS was asked and could not answer. A stale figure clearly labelled stale is
            // worth more at a counter than nothing at all — and strictly better than a fresh
            // figure that is silently wrong, which is why IsStale exists.
            facade._logger.LogWarning(
                "Live balance for account {AccountRef} failed ({Family}/{Code}); falling back to the snapshot",
                accountRef, balance.Family, balance.Code);

            return await facade.StaleBalanceAsync(tenantId, crmCustomerId, accountRef, ct);
        }

        /// <summary>
        /// What the tenant's installation can actually do. <see cref="IntegrationCapabilities.None"/>
        /// when there is no connection or no adapter: a screen then hides the buttons instead of
        /// offering one that answers a technical error.
        /// </summary>
        public IntegrationCapabilities GetCapabilities()
        {
            // Synchronous EF, deliberately: the interface method is synchronous and blocking on
            // an async query (.GetAwaiter().GetResult()) inside a request is how a thread pool is
            // starved. One indexed read by (tenant, family, is_active).
            var connection = facade._db.Connections
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == facade.TenantId
                            && c.Family == IntegrationFamily.CoreBanking
                            && c.IsActive)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefault();

            if (connection is null) return IntegrationCapabilities.None;

            var adapter = facade._resolver.ResolveFor(connection);

            return adapter.IsSuccess ? adapter.Value.Capabilities : IntegrationCapabilities.None;
        }

        private static IReadOnlyList<T> Deserialise<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];

            try
            {
                return JsonSerializer.Deserialize<List<T>>(json, SnapshotJson) ?? [];
            }
            catch (JsonException)
            {
                // A snapshot column written by an older shape must not break a 360 screen: the
                // rest of the snapshot (totals, KYC tier, date) is still true and still shown.
                return [];
            }
        }
    }

    // ── Insurance ───────────────────────────────────────────────────────────

    /// <summary>
    /// Insurance, as the rest of the platform sees it (ASS-02).
    ///
    /// <para>
    /// The asymmetry with core banking is the whole difficulty: a tenant has at most ONE active
    /// CBS connection but may distribute for several insurers at once, so every method here has
    /// to work out WHICH connection it is about — and none of them takes one as an argument. The
    /// answer is never "the first one": it is derived from the data the request carries, through
    /// <c>integration_mapping</c> for a subscription and through <c>integration_reference</c> for
    /// everything that concerns an existing policy.
    /// </para>
    /// </summary>
    internal sealed class InsuranceGateway(IntegrationModuleFacade facade) : IInsuranceGateway
    {
        public async Task<IntegrationCommandId> RequestPolicySubscriptionAsync(
            InsurancePolicyPayload payload, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(payload);

            var tenantId = facade.TenantId;
            var connection = await facade.ResolveInsurerForProductAsync(tenantId, payload.CrmProductId, ct);

            // ── Which CRM entity this command is about ──────────────────────
            //
            // M01 now owns a contract record — ProductHolding (BNF-B-14) — so a subscription
            // driven by one is keyed by the HOLDING and its clause version. That is what lets the
            // return path name the holding the insurer answered about, and what makes an avenant a
            // second send instead of a swallowed replay.
            //
            // Without a holding the old keying stands: by customer and product, because the caller
            // has no CRM-side contract record to point at. The comment that used to say no module
            // owns one is no longer true, and the two cases are now distinguished rather than
            // conflated.
            var (crmId, idempotencyKey) = payload.CrmHoldingId is { } holdingId
                ? (holdingId,
                   IdempotencyKeyFactory.ForHoldingSubscription(
                       tenantId, connection.Id, holdingId, payload.ClauseVersion))
                : (payload.CrmCustomerId,
                   IdempotencyKeyFactory.ForPolicySubscription(
                       tenantId, connection.Id, payload.CrmCustomerId,
                       payload.CrmProductId, payload.EffectiveDate));

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.SubscribePolicy,
                IntegrationEntityTypes.Policy,
                crmId,
                idempotencyKey,
                // The beneficiaries carry names and dates of birth, so this payload is genuinely
                // sensitive and genuinely encrypted. Only its top-level field names are in clear.
                facade._protector.Protect(payload),
                ct);
        }

        public async Task<IntegrationCommandId> RequestClaimDeclarationAsync(
            InsuranceClaimPayload payload, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(payload);

            var tenantId = facade.TenantId;

            // The claim names a POLICY by the insurer's own id, so the connection is the one that
            // holds that policy — found through the reference table, which is unique in both
            // directions per connection and therefore answers unambiguously.
            var owner = await facade.ResolvePolicyOwnerAsync(tenantId, payload.PolicyId.Value, ct)
                ?? throw new DomainException(
                    $"{IntegrationErrors.ExternalEntityNotFound}: no connection of this tenant "
                    + $"holds a policy {payload.PolicyId}.");

            return await facade.EnqueueAsync(
                tenantId,
                owner.Connection,
                CommandType.DeclareClaim,
                IntegrationEntityTypes.Claim,
                owner.CrmCustomerId,
                IdempotencyKeyFactory.ForClaimDeclaration(
                    tenantId, owner.Connection.Id, owner.CrmCustomerId,
                    payload.PolicyId.Value, payload.OccurredOn, payload.Nature),
                facade._protector.Protect(payload),
                ct);
        }

        public async Task<IntegrationCommandId> RequestClaimDocumentAsync(
            ClaimDocumentPayload payload, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(payload);

            var tenantId = facade.TenantId;

            // Through the CLAIM's own insurer reference, which is the only unambiguous route: the
            // dossier names the connection, and a piece joins a dossier.
            var connection = await facade.ResolveClaimInsurerAsync(tenantId, payload.ExternalClaimId, ct)
                ?? throw new DomainException(
                    $"{IntegrationErrors.ExternalEntityNotFound}: no active insurance connection "
                    + $"of this tenant holds a claim {payload.ExternalClaimId}.");

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.AddClaimDocument,
                IntegrationEntityTypes.Claim,
                // The DOCUMENT, so a command found in the rejection queue says which piece it was
                // for — the dossier alone would not distinguish three attachments.
                payload.ClaimDocumentId,
                IdempotencyKeyFactory.ForClaimDocument(
                    tenantId, connection.Id, payload.ClaimDocumentId),
                // Encrypted like every payload. It carries a FILE NAME, which an agent names after
                // the customer often enough to be personal data — the bytes themselves are never
                // here, only the store reference.
                facade._protector.Protect(payload),
                ct);
        }

        /// <summary>
        /// Every policy the customer holds, across every insurer the tenant distributes for.
        ///
        /// <para>
        /// Read live from each insurer rather than from a local read model, because this module
        /// owns no policy table — ASS-07's projection is another story. A connection that cannot
        /// answer is skipped with a log and the others are still returned: a partial list of real
        /// policies is useful at a counter, and one insurer's outage must not blank the screen.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<InsurancePolicy>> GetPoliciesAsync(
            Guid crmCustomerId, CancellationToken ct)
        {
            var tenantId = facade.TenantId;
            var policies = new List<InsurancePolicy>();

            foreach (var connection in await facade.ActiveInsuranceConnectionsAsync(tenantId, ct))
            {
                var customer = await facade._references.GetExternalIdAsync(
                    tenantId, connection.Id, IntegrationEntityTypes.Customer, crmCustomerId, ct);

                // Unknown to this insurer: no policies there, which is not a failure.
                if (customer is null) continue;

                var port = facade.ResolveInsurancePort<IInsurancePolicyPort>(
                    connection, IntegrationCapability.ReadPolicies);

                if (port is null) continue;

                var result = await port.GetPoliciesAsync(new ExternalId(customer), ct);

                if (result.IsSuccess)
                    policies.AddRange(result.Value);
                else
                    facade._logger.LogWarning(
                        "Insurer connection {ConnectionId} could not list policies ({Family}/{Code})",
                        connection.Id, result.Family, result.Code);
            }

            return policies;
        }

        /// <summary>
        /// Claims are declared against a policy, so they are read per policy: the customer's
        /// policy references on each connection give the ids to ask for.
        /// </summary>
        public async Task<IReadOnlyList<InsuranceClaim>> GetClaimsAsync(
            Guid crmCustomerId, CancellationToken ct)
        {
            var tenantId = facade.TenantId;
            var claims = new List<InsuranceClaim>();

            foreach (var connection in await facade.ActiveInsuranceConnectionsAsync(tenantId, ct))
            {
                var policyIds = await facade._db.References
                    .IgnoreQueryFilters()
                    .Where(r => r.TenantId == tenantId
                                && r.ConnectionId == connection.Id
                                && r.EntityType == IntegrationEntityTypes.Policy
                                && r.CrmId == crmCustomerId)
                    .Select(r => r.ExternalId)
                    .ToListAsync(ct);

                if (policyIds.Count == 0) continue;

                var port = facade.ResolveInsurancePort<IInsuranceClaimPort>(
                    connection, IntegrationCapability.ReadClaims);

                if (port is null) continue;

                foreach (var policyId in policyIds)
                {
                    var result = await port.GetClaimsAsync(new ExternalId(policyId), ct);

                    if (result.IsSuccess)
                        claims.AddRange(result.Value);
                    else
                        facade._logger.LogWarning(
                            "Insurer connection {ConnectionId} could not list claims of policy "
                            + "{PolicyId} ({Family}/{Code})",
                            connection.Id, policyId, result.Family, result.Code);
                }
            }

            return claims;
        }

        /// <summary>
        /// Per connection, unlike the core-banking one: each insurer has its own adapter and its
        /// own supported operations, and a screen showing two insurers must be able to offer
        /// "declare a claim" for one and not for the other.
        ///
        /// <para>
        /// <b>And it now honours its own parameter.</b> Until L8 the id selected the connection and
        /// the connection selected an adapter BY KIND, after which a parameterless
        /// <c>Capabilities</c> property answered for whichever row the adapter had bound itself to
        /// — so two active ORASS connections, the normal shape for an IMF selling IARD and Vie,
        /// got the same matrix. <see cref="ResolvedAdapter.Capabilities"/> applies the connection
        /// that was asked about, which is the only reason this method differs from
        /// <see cref="CoreBankingGateway.GetCapabilities"/> at all.
        /// </para>
        /// </summary>
        public IntegrationCapabilities GetCapabilities(Guid connectionId)
        {
            var connection = facade._db.Connections
                .IgnoreQueryFilters()
                .FirstOrDefault(c => c.TenantId == facade.TenantId && c.Id == connectionId);

            if (connection is null) return IntegrationCapabilities.None;

            var adapter = facade._resolver.ResolveFor(connection);

            return adapter.IsSuccess ? adapter.Value.Capabilities : IntegrationCapabilities.None;
        }
    }

    // ── Shared reads ────────────────────────────────────────────────────────

    /// <summary>
    /// Proves that an account reference belongs to a customer, and answers the identifier to call
    /// the external system WITH — or <c>null</c>, which means "do not call".
    ///
    /// <para>
    /// In the facade and not in an endpoint, because the facade is the choke point every caller
    /// goes through, this module's own dispatcher included. A check living in the INT-15 endpoint
    /// would leave the gateway unsafe for the next caller, and the next caller is the insurance
    /// premium debit of ASS-05 — which also takes an account reference and moves money.
    /// </para>
    ///
    /// <para>
    /// Two sources, in this order, because neither is sufficient alone:
    /// </para>
    ///
    /// <list type="number">
    /// <item><b><c>integration_reference</c> is authoritative.</b> We wrote the row ourselves
    ///   when the account was opened through SANKORE (INT-07), so it cannot be stale. But it only
    ///   knows accounts SANKORE opened, and importing an IMF's pre-existing portfolio is out of
    ///   scope — a reference-only check would deny the legitimate majority of real reads.</item>
    /// <item><b>The snapshot's account list covers the rest.</b> The synchronisation lists what
    ///   the CBS reports (INT-21), including accounts we did not open. It can be stale, or absent
    ///   for a customer never synced, which is why it is the second source and not the first.
    ///   Matched on either the internal id OR the account number: a screen will plausibly hand
    ///   back the human-readable number rather than the id.</item>
    /// </list>
    /// </summary>
    private async Task<ExternalId?> ResolveOwnedAccountAsync(
        Guid tenantId, Guid connectionId, Guid crmCustomerId, string accountRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accountRef)) return null;

        var trimmed = accountRef.Trim();

        // 1. The references we wrote. Equality on the external id, scoped to this customer and
        // this connection — the same four columns the unique index covers.
        var owned = await _db.References
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                        && r.ConnectionId == connectionId
                        && r.EntityType == IntegrationEntityTypes.Account
                        && r.CrmId == crmCustomerId
                        && r.ExternalId == trimmed)
            .Select(r => r.ExternalId)
            .FirstOrDefaultAsync(ct);

        if (owned is not null) return new ExternalId(owned);

        // 2. The snapshot, which is the only thing that knows a portfolio we did not open.
        var account = (await SnapshotAccountsAsync(tenantId, crmCustomerId, ct))
            .FirstOrDefault(a => a.AccountId.Value == trimmed || a.AccountNumber == trimmed);

        return account is null ? null : account.AccountId;
    }

    /// <summary>
    /// The accounts the last synchronisation reported for a customer, or an empty list. One
    /// reader for both the ownership check and the stale-balance fallback, so the two can never
    /// disagree about what the customer holds.
    /// </summary>
    private async Task<IReadOnlyList<CbsAccount>> SnapshotAccountsAsync(
        Guid tenantId, Guid crmCustomerId, CancellationToken ct)
    {
        var snapshot = await ReadSnapshotAsync(tenantId, crmCustomerId, ct);
        if (snapshot is null) return [];

        try
        {
            return JsonSerializer.Deserialize<List<CbsAccount>>(snapshot.AccountsJson, SnapshotJson) ?? [];
        }
        catch (JsonException)
        {
            // A column written by an older shape must not make an ownership check PASS. An empty
            // list denies the read, which is the safe direction.
            _logger.LogWarning(
                "Snapshot accounts of customer {CrmCustomerId} could not be read", crmCustomerId);

            return [];
        }
    }

    private Task<CbsSnapshot?> ReadSnapshotAsync(Guid tenantId, Guid crmCustomerId, CancellationToken ct)
        => _db.CbsSnapshots
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.CrmCustomerId == crmCustomerId, ct);

    /// <summary>
    /// The balance of one account as of the last synchronisation, flagged stale. <c>null</c> when
    /// the snapshot does not hold that account — which is the honest answer: inventing a zero
    /// balance for an account we have never seen is exactly the mistake
    /// <c>IKycModule.GetFlowUsageAsync</c> refuses to make.
    /// </summary>
    private async Task<CbsBalance?> StaleBalanceAsync(
        Guid tenantId, Guid crmCustomerId, string accountRef, CancellationToken ct)
    {
        var snapshot = await ReadSnapshotAsync(tenantId, crmCustomerId, ct);
        if (snapshot is null) return null;

        // Matched on either identifier, through the same reader the ownership check uses: a
        // caller holding a snapshot shows the account NUMBER to the clerk while the CBS keys its
        // own calls on the internal id, and both legitimately come back here as "the account
        // reference". Still scoped to this customer's own accounts — the fallback must not answer
        // for an account the ownership check would have refused.
        var trimmed = accountRef.Trim();

        var account = (await SnapshotAccountsAsync(tenantId, crmCustomerId, ct))
            .FirstOrDefault(a => a.AccountId.Value == trimmed || a.AccountNumber == trimmed);

        if (account is null) return null;

        return new CbsBalance(
            AccountId: account.AccountId,
            Currency: account.Currency,
            Balance: account.Balance,
            AvailableBalance: account.AvailableBalance,
            // The moment the figure was true, which is the snapshot's date and not now.
            AsOf: snapshot.SnapshotAt,
            IsStale: true);
    }

    private async Task<IReadOnlyList<IntegrationConnection>> ActiveInsuranceConnectionsAsync(
        Guid tenantId, CancellationToken ct)
        => await _db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                        && c.Family == IntegrationFamily.Insurance
                        && c.IsActive)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Which insurer a CRM product is distributed through.
    ///
    /// <para>
    /// Answered by <c>integration_mapping</c>, whose <c>Product</c> domain is per connection
    /// precisely because the same CRM product has a different code at each insurer. A product
    /// mapped nowhere falls back to the tenant's single active insurance connection — the common
    /// case of an IMF with one insurer, which must not be made to fill a mapping table to
    /// subscribe anybody. With several insurers and no mapping there is no defensible choice, so
    /// it refuses by name rather than picking the oldest connection and binding the customer to
    /// an insurer nobody chose.
    /// </para>
    /// </summary>
    private async Task<IntegrationConnection> ResolveInsurerForProductAsync(
        Guid tenantId, Guid crmProductId, CancellationToken ct)
    {
        var connections = await ActiveInsuranceConnectionsAsync(tenantId, ct);

        if (connections.Count == 0)
            throw new DomainException(
                $"{IntegrationErrors.NoActiveConnection}: this tenant has no active insurance connection.");

        var crmCode = crmProductId.ToString();
        var connectionIds = connections.Select(c => c.Id).ToList();

        var mappedConnectionId = await _db.Mappings
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId
                        && m.Domain == MappingDomain.Product
                        && m.CrmCode == crmCode
                        // A List and not an array: on .NET 10 an array's Contains binds to the
                        // ReadOnlySpan<T> extension and no longer translates to SQL.
                        && connectionIds.Contains(m.ConnectionId))
            .Select(m => (Guid?)m.ConnectionId)
            .FirstOrDefaultAsync(ct);

        if (mappedConnectionId is { } id)
            return connections.First(c => c.Id == id);

        if (connections.Count == 1) return connections[0];

        throw new DomainException(
            $"{IntegrationErrors.MappingMissing}: product {crmProductId} is mapped to none of this "
            + $"tenant's {connections.Count} active insurance connections, so the insurer to "
            + "subscribe with cannot be determined.");
    }

    /// <summary>
    /// The connection and the CRM entity behind one insurer-side policy id. <c>null</c> when no
    /// connection of the tenant knows that policy.
    ///
    /// <para>
    /// <b>Two sources, in this order, and the order is the whole point.</b> The reference table
    /// first: a policy this platform SOLD has one, its <c>CrmId</c> is the holding
    /// (BNF-B-16) and naming the holding is what lets the return path say which contract the
    /// insurer answered about. Then <c>ins_policy</c>: a policy sold at the insurer's own counter
    /// and discovered by ASS-07's sync has no reference row at all, and before this fallback
    /// existed a claim could not be declared on one — which is the ENTIRE population that
    /// synchronisation exists to bring in. Its <c>CrmId</c> is then the customer, which is what
    /// the read model carries.
    /// </para>
    ///
    /// <para>
    /// The connection comes from <c>ins_policy.connection_id</c> in that second case rather than
    /// from a sweep, so it is the row's own insurer and not the first one that happens to answer.
    /// </para>
    /// </summary>
    private async Task<PolicyOwner?> ResolvePolicyOwnerAsync(
        Guid tenantId, string externalPolicyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalPolicyId)) return null;

        var connections = await ActiveInsuranceConnectionsAsync(tenantId, ct);

        foreach (var connection in connections)
        {
            var crmId = await _references.GetCrmIdAsync(
                tenantId, connection.Id, IntegrationEntityTypes.Policy, externalPolicyId, ct);

            if (crmId is { } found) return new PolicyOwner(connection, found);
        }

        // IgnoreQueryFilters with an explicit tenant predicate: this runs from background work as
        // well as from a request, so the ambient tenant is not trusted to be the one asked about.
        var synced = await _db.Policies
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.ExternalPolicyId == externalPolicyId)
            .Select(p => new { p.ConnectionId, p.CrmCustomerId })
            .FirstOrDefaultAsync(ct);

        if (synced is null) return null;

        // Still only an ACTIVE connection: a deactivated insurer must not receive a declaration,
        // and the read model keeps its rows after a connection is switched off.
        var owner = connections.FirstOrDefault(c => c.Id == synced.ConnectionId);

        return owner is null ? null : new PolicyOwner(owner, synced.CrmCustomerId);
    }

    /// <summary>
    /// The active insurance connection holding one insurer-side claim id, read from this module's
    /// own claim register. <c>null</c> when no connection of the tenant knows that dossier.
    ///
    /// <para>
    /// From <c>ins_claim</c> and not from the reference table, unlike a policy: a dossier records
    /// its <c>connection_id</c> at declaration, so there is no sweep and no first-one-to-answer.
    /// Still only an ACTIVE connection — a deactivated insurer must not receive an attachment, and
    /// the register keeps its rows after a connection is switched off.
    /// </para>
    /// </summary>
    private async Task<IntegrationConnection?> ResolveClaimInsurerAsync(
        Guid tenantId, string externalClaimId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalClaimId)) return null;

        // IgnoreQueryFilters with an explicit tenant predicate: this is reached from a background
        // job as well as from a request.
        var connectionId = await _db.Claims
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.ExternalClaimId == externalClaimId)
            .Select(c => (Guid?)c.ConnectionId)
            .FirstOrDefaultAsync(ct);

        if (connectionId is not { } found) return null;

        var connections = await ActiveInsuranceConnectionsAsync(tenantId, ct);

        return connections.FirstOrDefault(c => c.Id == found);
    }

    /// <summary>
    /// A port of the adapter serving one insurance connection, or <c>null</c> when the connection
    /// has no adapter or the adapter does not declare the capability. Null rather than a result
    /// because both callers do the same thing with it: skip that insurer and carry on.
    ///
    /// <para>
    /// <b>Called in a LOOP over the tenant's active insurance connections</b>, by
    /// <c>GetPoliciesAsync</c> and <c>GetClaimsAsync</c>, inside ONE DI scope. That is why the
    /// capability question had to carry its connection rather than be cached on the scoped
    /// adapter: the same adapter instance is asked about several rows one after another, so an
    /// answer cached per scope is an answer for whichever row came first. The adapters stay scoped
    /// — the lifetime was never the problem — and <see cref="ResolvedAdapter"/> re-pairs on every
    /// iteration.
    /// </para>
    /// </summary>
    private TPort? ResolveInsurancePort<TPort>(
        IntegrationConnection connection, IntegrationCapability capability) where TPort : class
    {
        var adapter = _resolver.ResolveFor(connection);
        if (adapter.IsFailure)
        {
            _logger.LogWarning(
                "Insurance connection {ConnectionId} of kind {Kind} has no registered adapter",
                connection.Id, connection.Kind);

            return null;
        }

        var port = _resolver.ResolvePort<TPort>(adapter.Value, capability);

        return port.IsSuccess ? port.Value : null;
    }

    private sealed record PolicyOwner(IntegrationConnection Connection, Guid CrmCustomerId);

    /// <summary>
    /// Telephony (TEL-B-02). Reads only — see <see cref="ITelephonyGateway"/> for why the two
    /// agent acts are not on this contract.
    /// </summary>
    internal sealed class TelephonyGateway(IntegrationModuleFacade facade) : ITelephonyGateway
    {
        /// <summary>
        /// Delegates to the screen-pop query, so there is ONE definition of "who is on this call"
        /// and of the perimeter it applies. A second projection here would be a second place the
        /// name of an out-of-perimeter record could be allowed through.
        /// </summary>
        public async Task<TelephonyCallContext?> GetCallContextAsync(
            Guid callId, CancellationToken ct)
        {
            // Required here and nowhere else — see the field. Throwing beats answering null: a
            // null reads as "no such call", and a screen pop that quietly showed nobody would be
            // diagnosed as a matching problem rather than a wiring one.
            ArgumentNullException.ThrowIfNull(facade._sender);

            var result = await facade._sender.Send(
                new Features.Telephony.CallContext.GetCallContextQuery(callId), ct);

            return result.IsSuccess ? result.Value : null;
        }

        /// <summary>
        /// The calls of one record, newest first.
        ///
        /// <para>
        /// <b>No agency perimeter and no permission here, deliberately.</b> The caller is another
        /// module projecting this into a record it has already decided the user may see — M01's
        /// client 360, M13's lead timeline — and those screens carry their own gate. Applying a
        /// second one here would hide an agency's own calls from the client file that agency owns.
        /// The summary carries no number and no note for the same reason the timeline projection
        /// of M13 carries none: this crosses into a store with no reveal audit.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<TelephonyCallSummary>> GetCallsForEntityAsync(
            TelephonyEntityRef entity, int page, int pageSize, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(entity);

            if (!Domain.TelEntityTypes.IsKnown(entity.Type) || entity.Id == Guid.Empty)
                return [];

            var size = Math.Clamp(pageSize, 1, 200);
            var skip = (Math.Max(1, page) - 1) * size;

            return await facade._db.TelCalls
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == facade.TenantId
                         && c.EntityType == entity.Type
                         && c.EntityId == entity.Id)
                .OrderByDescending(c => c.StartedAt)
                .ThenBy(c => c.Id)
                .Skip(skip)
                .Take(size)
                .Select(c => new TelephonyCallSummary(
                    c.Id,
                    c.Direction.ToString(),
                    c.Status.ToString(),
                    c.StartedAt,
                    c.TalkSeconds,
                    c.OutcomeCode,
                    c.CrmUserId))
                .ToListAsync(ct);
        }

        /// <summary>
        /// What the tenant's telephony installation can do. <see cref="IntegrationCapabilities.None"/>
        /// when there is no connection or no adapter — a screen then hides the call button rather
        /// than offering one that answers a technical error.
        /// </summary>
        public IntegrationCapabilities GetCapabilities()
        {
            // Synchronous EF for the same reason as the core banking twin: the interface method
            // is synchronous, and blocking on an async query inside a request starves the pool.
            var connection = facade._db.Connections
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == facade.TenantId
                            && c.Family == IntegrationFamily.Telephony
                            && c.IsActive)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefault();

            if (connection is null) return IntegrationCapabilities.None;

            var adapter = facade._resolver.ResolveFor(connection);

            return adapter.IsSuccess ? adapter.Value.Capabilities : IntegrationCapabilities.None;
        }
    }

    /// <summary>
    /// WhatsApp (WA-BE-01, WA-BE-02). Thin on purpose: the directory and the port resolution are
    /// <see cref="Features.Messaging.MessagingChannels"/>'s, and the decisions — who may send what
    /// to whom — are M13's. What this adds is the one rule that belongs to the channel: an inactive
    /// number sends nothing.
    /// </summary>
    internal sealed class MessagingGateway(IntegrationModuleFacade facade) : IMessagingGateway
    {
        private Features.Messaging.MessagingChannels Channels
            => facade._messaging ?? throw new InvalidOperationException(
                "MessagingChannels is not registered: AddIntegrationModule must run before the "
                + "messaging gateway is used.");

        public async Task<MessagingChannelInfo?> ResolveChannelAsync(
            Guid tenantId, Guid? agencyId, CancellationToken ct)
        {
            var connection = await Channels.ResolveForAgencyAsync(tenantId, agencyId, ct);
            return connection is null ? null : Info(connection);
        }

        public async Task<MessagingChannelInfo?> GetChannelAsync(
            Guid tenantId, Guid connectionId, CancellationToken ct)
        {
            var connection = await Channels.GetAsync(tenantId, connectionId, ct);
            return connection is null ? null : Info(connection);
        }

        public async Task<IReadOnlyList<MessagingChannelInfo>> ListChannelsAsync(
            Guid tenantId, CancellationToken ct)
            => [.. (await Channels.ListAsync(tenantId, activeOnly: false, ct)).Select(Info)];

        public async Task<IntegrationResult<IReadOnlyList<MessagingTemplateInfo>>> ListTemplatesAsync(
            Guid tenantId, Guid connectionId, CancellationToken ct)
        {
            var port = await PortAsync(tenantId, connectionId, IntegrationCapability.TemplateCatalog, requireActive: false, ct);
            if (port.Failure is { } failure) return Forward<IReadOnlyList<MessagingTemplateInfo>>(failure);

            var templates = await port.Port!.ListTemplatesAsync(port.Connection!, ct);

            return templates.IsSuccess
                ? IntegrationResult.Ok<IReadOnlyList<MessagingTemplateInfo>>([.. templates.Value.Select(t =>
                    new MessagingTemplateInfo(
                        t.ProviderTemplateId, t.Name, t.Language, t.Category, t.Status,
                        t.HeaderText, t.BodyText, t.FooterText, t.Buttons))])
                : Forward<IReadOnlyList<MessagingTemplateInfo>>(templates);
        }

        public async Task<IntegrationResult<string>> SendTemplateAsync(
            Guid tenantId, Guid connectionId, MessagingTemplateSend message, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(message);

            var port = await PortAsync(tenantId, connectionId, IntegrationCapability.TemplateMessages, requireActive: true, ct);
            if (port.Failure is { } failure) return Forward<string>(failure);

            return await port.Port!.SendTemplateAsync(
                port.Connection!,
                new OutboundTemplate(message.ToE164, message.Name, message.Language, message.BodyParameters),
                ct);
        }

        public async Task<IntegrationResult<string>> SendTextAsync(
            Guid tenantId, Guid connectionId, string toE164, string text, CancellationToken ct)
        {
            var port = await PortAsync(tenantId, connectionId, IntegrationCapability.FreeformMessages, requireActive: true, ct);
            if (port.Failure is { } failure) return Forward<string>(failure);

            return await port.Port!.SendTextAsync(port.Connection!, toE164, text, ct);
        }

        public async Task<IntegrationResult<MessagingMediaSent>> SendMediaAsync(
            Guid tenantId, Guid connectionId, string toE164, MessagingMediaContent media, string? caption, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(media);

            var port = await PortAsync(tenantId, connectionId, IntegrationCapability.MediaMessages, requireActive: true, ct);
            if (port.Failure is { } failure) return Forward<MessagingMediaSent>(failure);

            var sent = await port.Port!.SendMediaAsync(
                port.Connection!, toE164,
                new OutboundMedia(media.Content, media.ContentType, media.FileName ?? "fichier", caption), ct);

            return sent.IsSuccess
                ? IntegrationResult.Ok(new MessagingMediaSent(sent.Value.ProviderMessageId, sent.Value.MediaId))
                : Forward<MessagingMediaSent>(sent);
        }

        public async Task<IntegrationResult<MessagingMediaContent>> DownloadMediaAsync(
            Guid tenantId, Guid connectionId, string mediaId, CancellationToken ct)
        {
            // Not gated on the number being ACTIVE: a media received last week must stay readable
            // after the number was switched off, for as long as the provider keeps it.
            var port = await PortAsync(tenantId, connectionId, IntegrationCapability.MediaMessages, requireActive: false, ct);
            if (port.Failure is { } failure) return Forward<MessagingMediaContent>(failure);

            var media = await port.Port!.DownloadMediaAsync(port.Connection!, mediaId, ct);

            return media.IsSuccess
                ? IntegrationResult.Ok(new MessagingMediaContent(media.Value.Content, media.Value.ContentType, media.Value.FileName))
                : Forward<MessagingMediaContent>(media);
        }

        public async Task<IntegrationResult> DeleteMediaAsync(
            Guid tenantId, Guid connectionId, string mediaId, CancellationToken ct)
        {
            // Not gated on ACTIVE either: retention must reach a number switched off since.
            var port = await PortAsync(tenantId, connectionId, IntegrationCapability.MediaMessages, requireActive: false, ct);
            if (port.Failure is { } failure) return failure;

            return await port.Port!.DeleteMediaAsync(port.Connection!, mediaId, ct);
        }

        private async Task<(IntegrationConnection? Connection, IConversationalChannelPort? Port, IntegrationResult? Failure)> PortAsync(
            Guid tenantId, Guid connectionId, IntegrationCapability capability, bool requireActive, CancellationToken ct)
        {
            var connection = await Channels.GetAsync(tenantId, connectionId, ct);

            if (connection is null || (requireActive && !connection.IsActive))
            {
                return (null, null, IntegrationResult.Technical(
                    IntegrationErrors.MessagingChannelInactive,
                    "This WhatsApp number is not connected, or has been switched off."));
            }

            var port = Channels.PortFor(connection, capability);

            return port.IsFailure
                ? (null, null, port)
                : (connection, port.Value, null);
        }

        private static MessagingChannelInfo Info(IntegrationConnection connection)
            => new(
                connection.Id,
                connection.Name,
                connection.Kind.ToString(),
                Features.Messaging.MessagingChannels.DisplayNumberOf(connection),
                Features.Messaging.MessagingChannels.AgencyOf(connection),
                connection.IsActive);

        private static IntegrationResult<T> Forward<T>(IntegrationResult failure) => failure.Family switch
        {
            ErrorFamily.Transient => IntegrationResult.Transient<T>(failure.Code!, failure.Detail),
            ErrorFamily.Functional => IntegrationResult.Functional<T>(failure.Code!, failure.Detail),
            _ => IntegrationResult.Technical<T>(failure.Code!, failure.Detail),
        };
    }
}
