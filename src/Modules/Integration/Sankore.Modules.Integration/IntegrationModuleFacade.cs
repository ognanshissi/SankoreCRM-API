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
            services.GetService<Features.Balance.LiveBalanceGate>())
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
        Features.Balance.LiveBalanceGate? balanceGate = null)
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

        CoreBanking = new CoreBankingGateway(this);
        Insurance = new InsuranceGateway(this);
    }

    public ICoreBankingGateway CoreBanking { get; }

    public IInsuranceGateway Insurance { get; }

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

            var adapter = resolved.Value.Adapter;
            var connectionId = resolved.Value.Connection.Id;

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

            var adapter = facade._resolver.ResolveAdapter(connection);

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

            return await facade.EnqueueAsync(
                tenantId,
                connection,
                CommandType.SubscribePolicy,
                IntegrationEntityTypes.Policy,
                // The policy has no CRM id of its own — no module owns a policy record yet — so
                // the command, and the reference it will produce, are keyed by the customer.
                payload.CrmCustomerId,
                IdempotencyKeyFactory.ForPolicySubscription(
                    tenantId, connection.Id, payload.CrmCustomerId,
                    payload.CrmProductId, payload.EffectiveDate),
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
        /// </summary>
        public IntegrationCapabilities GetCapabilities(Guid connectionId)
        {
            var connection = facade._db.Connections
                .IgnoreQueryFilters()
                .FirstOrDefault(c => c.TenantId == facade.TenantId && c.Id == connectionId);

            if (connection is null) return IntegrationCapabilities.None;

            var adapter = facade._resolver.ResolveAdapter(connection);

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
    /// The connection and the customer behind one insurer-side policy id, through the reference
    /// table. <c>null</c> when no connection of the tenant knows that policy.
    /// </summary>
    private async Task<PolicyOwner?> ResolvePolicyOwnerAsync(
        Guid tenantId, string externalPolicyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalPolicyId)) return null;

        foreach (var connection in await ActiveInsuranceConnectionsAsync(tenantId, ct))
        {
            var crmId = await _references.GetCrmIdAsync(
                tenantId, connection.Id, IntegrationEntityTypes.Policy, externalPolicyId, ct);

            if (crmId is { } found) return new PolicyOwner(connection, found);
        }

        return null;
    }

    /// <summary>
    /// A port of the adapter serving one insurance connection, or <c>null</c> when the connection
    /// has no adapter or the adapter does not declare the capability. Null rather than a result
    /// because both callers do the same thing with it: skip that insurer and carry on.
    /// </summary>
    private TPort? ResolveInsurancePort<TPort>(
        IntegrationConnection connection, IntegrationCapability capability) where TPort : class
    {
        var adapter = _resolver.ResolveAdapter(connection);
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
}
