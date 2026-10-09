namespace Sankore.Modules.Integration.Adapters.Fake;

using System.Globalization;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// In-process stand-in for a core banking system AND for an insurer back-office (INT-10).
///
/// One class for both families on purpose: the socle above it — dispatcher, command lifecycle,
/// reconciliation — is shared, and a developer running the whole chain locally needs one
/// registration, not two. Which family a connection means it stands in for is carried by
/// <see cref="FakeSettings.Family"/>, not by the adapter.
///
/// It exists for two audiences and must stay pleasant for both: the contract suite, which wants
/// one line to force a given outcome, and a developer running the integration flows with no CBS
/// and no insurer in reach.
///
/// <para>
/// <b>Deterministic.</b> Everything it answers is a pure function of its own properties and of
/// the order calls arrive in — no clock (see <see cref="FixedInstant"/>), no randomness, no
/// hashing of the payload. A test that asserts on a field must keep passing on the next run and
/// on someone else's machine; "plausible but varying" data is how a suite starts flaking.
/// </para>
///
/// <para>
/// <b><see cref="IdempotencyKey"/> is honoured.</b> The same write replayed under the same key
/// returns the SAME <see cref="ExternalId"/> and records ONE call. This is not a convenience:
/// it is the behaviour every real adapter must have, because a timeout leaves the dispatcher
/// unable to tell "not created" from "created, answer lost", and the contract suite asserts it
/// against every implementation. A double that created a second customer on replay would make
/// the dispatcher's retry look safe when it is not.
/// </para>
///
/// <para>
/// <b>Not thread-safe</b>, like any test double: the collections below are plain ones so a test
/// can mutate them in a line. The dispatcher calls an adapter from one job at a time.
/// </para>
/// </summary>
public sealed class FakeAdapter :
    ICbsAdapter,
    ICbsCustomerPort,
    ICbsKycLevelPort,
    ICbsAccountPort,
    ICbsTransactionPort,
    ICbsLoanPort,
    IInsuranceProductPort,
    IInsurancePolicyPort,
    IInsuranceClaimPort
{
    /// <summary>Currency of every seeded figure. XOF because the deployments are UEMOA ones.</summary>
    public const string SeededCurrency = "XOF";

    public const string SeededCustomerId = "CUS-CI-0001";
    public const string SeededAccountId = "ACC-CI-0001";
    public const string SeededLoanId = "LN-CI-0001";
    public const string SeededPolicyId = "POL-CI-0001";
    public const string SeededClaimId = "CLM-CI-0001";

    /// <summary>A CBS product code the seeded catalogue knows. Anything else is unmapped.</summary>
    public const string SeededProductCode = "EPARGNE-TONTINE";

    /// <summary>An insurer product code the seeded catalogue knows.</summary>
    public const string SeededInsurerProductCode = "ASS-DECES-EMPRUNTEUR";

    /// <summary>
    /// CRM identity of the seeded policy holder. Fixed, because a <c>Guid.NewGuid()</c> here
    /// would be the one varying value in an otherwise deterministic double.
    /// </summary>
    public static readonly Guid SeededCrmCustomerId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>Account status the fake will let money move on. Any other refuses functionally.</summary>
    public const string OperableAccountStatus = "ACTIVE";

    // ── Forced outcomes: null means "answer the plausible success below" ─────────────────────
    //
    // Three knobs rather than one because the useful scenarios are asymmetric: a duplicate
    // refuses the WRITE while reads keep answering (that is how the rejection queue is reached
    // with a readable system behind it), whereas an outage refuses everything.

    /// <summary>Forces every call — read, write and health — to fail this way.</summary>
    public IntegrationResult? ForcedOutcome { get; set; }

    /// <summary>Forces only the writes, leaving the reads answering. Wins over <see cref="ForcedOutcome"/>.</summary>
    public IntegrationResult? ForcedWriteOutcome { get; set; }

    /// <summary>Forces only the reads. Wins over <see cref="ForcedOutcome"/>.</summary>
    public IntegrationResult? ForcedReadOutcome { get; set; }

    /// <summary>Pins the health answer whatever the rest says — for the activation screen's paths.</summary>
    public IntegrationHealth? ForcedHealth { get; set; }

    /// <summary>
    /// Every call refused on the merits — "the far end answered, and said no". A functional
    /// refusal is never retried, so this is the factory for exercising the rejection queue.
    /// </summary>
    public static FakeAdapter Rejecting(string code) => new()
    {
        ForcedOutcome = IntegrationResult.Functional(code, $"The external system refused: {code}."),
    };

    /// <summary>
    /// Every call fails transiently — "we learned nothing, retry later". Separate from
    /// <see cref="Rejecting"/> because recording our own outage as a refusal would park a
    /// perfectly good command in a queue a human has to empty.
    /// </summary>
    public static FakeAdapter Unavailable(string code = IntegrationErrors.Unavailable) => new()
    {
        ForcedOutcome = IntegrationResult.Transient(code, $"The external system is unavailable: {code}."),
    };

    /// <summary>
    /// No answer within the budget. The one scenario a real adapter cannot distinguish from a
    /// successful write whose answer was lost — which is why the idempotency key exists.
    /// </summary>
    public static FakeAdapter TimingOut() => new()
    {
        ForcedOutcome = IntegrationResult.Transient(
            IntegrationErrors.Timeout, "No answer from the external system within the budget."),
    };

    /// <summary>
    /// Writes refused as already held by the far end, reads still answering. Deliberately NOT
    /// transient: the record exists, so another attempt would refuse again forever.
    /// </summary>
    public static FakeAdapter Duplicating() => new()
    {
        ForcedWriteOutcome = IntegrationResult.Functional(
            IntegrationErrors.Duplicate, "The external system already holds this record."),
    };

    /// <summary>
    /// Every capability declared in <see cref="CapabilityMode.Batch"/> — the extreme case for a
    /// screen deciding whether to offer a live button: nothing here answers in real time.
    ///
    /// <para>
    /// <b>No real adapter is shaped like this, and the double should not be read as a portrait of
    /// one.</b> It used to claim it resembled an Amplitude Legacy or a Perfect Vision installation;
    /// neither does. Both declare the batch WRITES and simply omit the reads
    /// (<c>PerfectVisionCapabilityMatrix</c>, <c>AmplitudeCapabilityMatrix</c>), because
    /// <c>CapabilityMode.Batch</c> names a carrier — a file produced at a cut-off and closed by an
    /// acknowledgement — and there is no <c>CommandType</c> for a read, so no read can be deferred
    /// to a file cycle. A <c>Batch</c> read promises a carrier the socle does not have.
    /// </para>
    ///
    /// <para>
    /// It stays as it is because a double's job is to let a test reach the branch it cares about,
    /// and "every capability, batch" is the widest way to reach the non-real-time branch. The
    /// inaccuracy was in the description, not in the behaviour.
    /// </para>
    /// </summary>
    public static FakeAdapter BatchOnly() => new()
    {
        Capabilities = IntegrationCapabilities.All(
            CapabilityMode.Batch, Enum.GetValues<IntegrationCapability>()),
    };

    // ── Knobs on the plausible success ──────────────────────────────────────────────────────

    public IntegrationKind Kind => IntegrationKind.Fake;

    /// <summary>
    /// Every capability, in real time — the answer for any connection that is not named in
    /// <see cref="CapabilitiesByConnection"/>. Settable so a test can build a batch-only or a
    /// crippled fake: the matrix is read from the adapter precisely because the answer depends on
    /// the installation, and a double that could only ever say "everything, live" would never
    /// exercise the screens that hide a button.
    /// </summary>
    public IntegrationCapabilities Capabilities { get; set; } = IntegrationCapabilities.All(
        CapabilityMode.RealTime, Enum.GetValues<IntegrationCapability>());

    /// <summary>
    /// Per-connection overrides, for the one situation <see cref="Capabilities"/> alone cannot
    /// express: <b>two connections of the same kind whose matrices differ</b>.
    ///
    /// <para>
    /// That shape is not exotic, it is the insurance family's normal one. A tenant has at most one
    /// active core-banking connection, but ASS-01 deliberately permits several active insurance
    /// ones, and an IMF distributing IARD and Vie holds two rows of the same kind that resolve to
    /// the same adapter instance. Until <c>ICbsAdapter.CapabilitiesFor</c> took its connection
    /// there was no way for a double to answer differently for the two, which is precisely why the
    /// defect it fixes could not be caught by a test.
    /// </para>
    ///
    /// <para>
    /// Empty by default, so every existing test keeps the single-matrix behaviour.
    /// </para>
    /// </summary>
    public Dictionary<Guid, IntegrationCapabilities> CapabilitiesByConnection { get; } = [];

    /// <summary>
    /// The override for this connection, or <see cref="Capabilities"/>.
    ///
    /// <para>
    /// A null connection answers the default rather than throwing: this is a test double, and a
    /// harness that has no row to hand should still be able to ask what the fake can do.
    /// </para>
    /// </summary>
    public IntegrationCapabilities CapabilitiesFor(IntegrationConnection connection)
        => connection is not null
           && CapabilitiesByConnection.TryGetValue(connection.Id, out var specific)
            ? specific
            : Capabilities;

    /// <summary>
    /// The single instant every answer is stamped with. A constant and not
    /// <see cref="TimeProvider"/>: <c>AsOf</c>, <c>PostedAt</c> and <c>QuotedAt</c> are fields a
    /// test asserts on, and reading a clock would make those assertions depend on when the suite
    /// ran. Move it in a test to exercise staleness, don't let it tick.
    /// </summary>
    public DateTimeOffset FixedInstant { get; set; } = new(2024, 1, 15, 9, 30, 0, TimeSpan.Zero);

    /// <summary>Latency the health check reports. Not measured — measuring it would be a clock.</summary>
    public TimeSpan HealthLatency { get; set; } = TimeSpan.FromMilliseconds(42);

    /// <summary>
    /// External customer references the far end knows. An id absent from here is reported as
    /// <see cref="IntegrationErrors.ExternalEntityNotFound"/>, never as an exception: an entity
    /// the CBS does not have is an answer, and the dispatcher must be able to park it.
    /// </summary>
    public HashSet<string> Customers { get; } = [SeededCustomerId];

    /// <summary>
    /// Accounts per external customer reference. Keyed that way because the only account read
    /// the port offers starts from a customer; the per-account lookups scan.
    /// </summary>
    public Dictionary<string, List<CbsAccount>> AccountsByCustomer { get; } = new()
    {
        [SeededCustomerId] =
        [
            new CbsAccount(
                new ExternalId(SeededAccountId),
                AccountNumber: "CI0012345678",
                ProductCode: SeededProductCode,
                ProductLabel: "Épargne tontine",
                Currency: SeededCurrency,
                Balance: 125_000m,
                AvailableBalance: 120_000m,
                Status: OperableAccountStatus,
                OpenedOn: new DateOnly(2022, 6, 1)),
        ],
    };

    public Dictionary<string, List<CbsLoan>> LoansByCustomer { get; } = new()
    {
        [SeededCustomerId] =
        [
            new CbsLoan(
                new ExternalId(SeededLoanId),
                ProductCode: "CREDIT-PME",
                PrincipalAmount: 2_000_000m,
                OutstandingAmount: 1_450_000m,
                Currency: SeededCurrency,
                Status: "ACTIVE",
                DisbursedOn: new DateOnly(2023, 3, 15),
                MaturityOn: new DateOnly(2026, 3, 15),
                DaysInArrears: 0),
        ],
    };

    /// <summary>
    /// Five movements on the seeded account, so the default fake actually has more than one page
    /// at <see cref="TransactionPageSize"/>. Dates are fixed for the same reason
    /// <see cref="FixedInstant"/> is.
    /// </summary>
    public Dictionary<string, List<CbsTransaction>> TransactionsByAccount { get; } = new()
    {
        [SeededAccountId] =
        [
            new CbsTransaction("TRX-0001", new DateOnly(2024, 1, 5), null, 50_000m, SeededCurrency, "Credit", "Dépôt espèces", null),
            new CbsTransaction("TRX-0002", new DateOnly(2024, 1, 8), null, 12_500m, SeededCurrency, "Debit", "Retrait GAB", null),
            new CbsTransaction("TRX-0003", new DateOnly(2024, 1, 12), null, 75_000m, SeededCurrency, "Credit", "Virement salaire", "SOCIETE AGRO CI"),
            new CbsTransaction("TRX-0004", new DateOnly(2024, 1, 19), null, 7_500m, SeededCurrency, "Debit", "Prime assurance", null),
            new CbsTransaction("TRX-0005", new DateOnly(2024, 1, 26), null, 20_000m, SeededCurrency, "Debit", "Transfert mobile", "WAVE CI"),
        ],
    };

    /// <summary>
    /// Deliberately small so the cursor path is exercised by the DEFAULT fake rather than only by
    /// a test that remembers to shrink it. A pager that is never paged is a pager that breaks the
    /// first time a real CBS answers in pages.
    /// </summary>
    public int TransactionPageSize { get; set; } = 2;

    public decimal MonthlyCreditTotal { get; set; } = 125_000m;

    public decimal MonthlyDebitTotal { get; set; } = 40_000m;

    /// <summary>CBS product codes the far end knows. Anything else is an unmapped code.</summary>
    public HashSet<string> ProductCodes { get; } = [SeededProductCode, "CPTE-COURANT", "CREDIT-PME"];

    /// <summary>Insurer product codes the catalogue exposes (ASS-02).</summary>
    public HashSet<string> InsurerProductCodes { get; } = [SeededInsurerProductCode, "ASS-IARD-AUTO"];

    /// <summary>Policies per external customer reference.</summary>
    public Dictionary<string, List<InsurancePolicy>> PoliciesByCustomer { get; } = new()
    {
        [SeededCustomerId] =
        [
            new InsurancePolicy(
                new ExternalId(SeededPolicyId),
                PolicyNumber: "POL-2024-000001",
                CrmCustomerId: SeededCrmCustomerId,
                InsurerProductCode: SeededInsurerProductCode,
                Status: PolicyStatus.Issued,
                EffectiveDate: new DateOnly(2024, 1, 1),
                ExpiryDate: new DateOnly(2024, 12, 31),
                PremiumAmount: 7_500m,
                Currency: SeededCurrency,
                Periodicity: PremiumPeriodicity.Monthly,
                NextDueDate: new DateOnly(2024, 2, 1)),
        ],
    };

    public Dictionary<string, List<InsuranceClaim>> ClaimsByPolicy { get; } = new()
    {
        [SeededPolicyId] =
        [
            new InsuranceClaim(
                new ExternalId(SeededClaimId),
                ClaimNumber: "SIN-2024-000001",
                PolicyId: new ExternalId(SeededPolicyId),
                Status: ClaimStatus.UnderReview,
                OccurredOn: new DateOnly(2024, 1, 10),
                DeclaredAt: new DateTimeOffset(2024, 1, 11, 8, 0, 0, TimeSpan.Zero),
                IndemnityAmount: null,
                MissingDocuments: null),
        ],
    };

    /// <summary>
    /// Where <see cref="SubscribeAsync"/> files a new policy. The payload carries only a CRM
    /// customer id, and the external reference an insurer would resolve from its own party record
    /// is not in the contract — so the fake needs to be told once rather than guess per call.
    /// </summary>
    public ExternalId PolicyHolder { get; set; } = new(SeededCustomerId);

    /// <summary>Premium quoted when the caller names no insured amount.</summary>
    public decimal QuotedPremium { get; set; } = 7_500m;

    /// <summary>
    /// Rate applied to an insured amount when one is given. Derived rather than fixed so that
    /// moving the amount in a test never leaves a premium that contradicts it.
    /// </summary>
    public decimal PremiumRate { get; set; } = 0.015m;

    public PremiumPeriodicity QuotedPeriodicity { get; set; } = PremiumPeriodicity.Monthly;

    public bool IsEligible { get; set; } = true;

    /// <summary>
    /// Reasons reported when <see cref="IsEligible"/> is false. Never empty in that case: a
    /// refusal the agent cannot explain sends the customer away without knowing why (ASS-04).
    /// </summary>
    public List<string> IneligibilityReasons { get; } = ["AGE_ABOVE_PRODUCT_CEILING"];

    public string CertificateContentType { get; set; } = "application/pdf";

    /// <summary>
    /// Stands in for the insurer's PDF. Fixed bytes, not a generated document: a test asserts the
    /// certificate reached storage unchanged, which a varying payload cannot show.
    /// </summary>
    public byte[] CertificateContent { get; set; } = [0x25, 0x50, 0x44, 0x46];

    // ── What the double recorded ────────────────────────────────────────────────────────────

    /// <summary>
    /// One entry per call that actually reached the far end. A write replayed under a key already
    /// seen adds NOTHING here — that absence is what proves the replay was not a second write.
    /// </summary>
    public List<FakeAdapterCall> Calls { get; } = [];

    public IdempotencyKey? LastIdempotencyKey { get; private set; }

    public CbsCustomerPayload? LastCustomerPayload { get; private set; }

    public CbsLoanApplicationPayload? LastLoanApplication { get; private set; }

    public InsurancePolicyPayload? LastPolicyPayload { get; private set; }

    public InsuranceClaimPayload? LastClaimPayload { get; private set; }

    public CbsDebitReceipt? LastDebitReceipt { get; private set; }

    /// <summary>The tier the last <see cref="SetKycLevelAsync"/> pushed, per external customer.</summary>
    public Dictionary<string, KycLevel> KycLevels { get; } = [];

    /// <summary>Storage references added to a claim after its declaration, in order.</summary>
    public List<string> AddedClaimDocuments { get; } = [];

    /// <summary>The connection the last health check was asked about.</summary>
    public IntegrationConnection? LastHealthConnection { get; private set; }

    /// <summary>The cursor the last transaction page was asked for. Null means "from the start".</summary>
    public string? LastTransactionCursor { get; private set; }

    /// <summary>
    /// Writes already answered, keyed by operation and idempotency key. The value is the external
    /// id (or the debit reference) the first attempt produced, which is what a replay must get
    /// back.
    /// </summary>
    private readonly Dictionary<string, string> _answeredWrites = [];

    /// <summary>Debit references this double issued, with what they moved, for the reversal.</summary>
    private readonly Dictionary<string, (string AccountId, decimal Amount, string Currency)> _debits = [];

    /// <summary>
    /// One sequence for every generated reference. Shared across families so two ids can never
    /// collide, and incremented only on a real write so a replay does not burn a number.
    /// </summary>
    private int _sequence;

    // ── ICbsAdapter ─────────────────────────────────────────────────────────────────────────

    public Task<IntegrationHealth> CheckHealthAsync(IntegrationConnection connection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(connection);

        Record(FakeAdapterOperations.CheckHealth, connection.Id.ToString(), key: null);
        LastHealthConnection = connection;

        if (ForcedHealth is not null) return Task.FromResult(ForcedHealth);

        // The row's own FakeSettings win over the in-process knobs here, and ONLY here:
        // CheckHealthAsync is the single method that receives a connection, so it is the only
        // place the per-connection forcing FakeSettings advertises can be honoured at all. The
        // port methods get no connection and therefore read the Forced* properties instead.
        if (connection.Settings is FakeSettings settings
            && !string.IsNullOrWhiteSpace(settings.ForcedErrorCode))
        {
            return Task.FromResult(IntegrationHealth.Unhealthy(
                settings.ForcedErrorCode, FixedInstant, HealthLatency));
        }

        var forced = ForcedOutcome ?? ForcedReadOutcome;

        return Task.FromResult(forced is { IsFailure: true }
            ? IntegrationHealth.Unhealthy(forced.Code!, FixedInstant, HealthLatency)
            : IntegrationHealth.Healthy(HealthLatency, FixedInstant, "In-memory double — no back-office."));
    }

    // ── ICbsCustomerPort ────────────────────────────────────────────────────────────────────

    public Task<IntegrationResult<ExternalId>> CreateCustomerAsync(
        CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);

        return Task.FromResult(Write<ExternalId>(
            FakeAdapterOperations.CreateCustomer, payload.CrmCustomerId.ToString(), key,
            () =>
            {
                LastCustomerPayload = payload;
                var id = NextReference("CUS");
                Customers.Add(id);
                return IntegrationResult.Ok(new ExternalId(id));
            },
            replay: existing => IntegrationResult.Ok(new ExternalId(existing))));
    }

    public Task<IntegrationResult> UpdateCustomerAsync(
        ExternalId id, CbsCustomerPayload payload, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);

        return Task.FromResult(VoidWrite(
            FakeAdapterOperations.UpdateCustomer, id.Value, key,
            () =>
            {
                if (!Customers.Contains(id.Value)) return NotFound("customer", id.Value);
                LastCustomerPayload = payload;
                return IntegrationResult.Ok();
            }));
    }

    // ── ICbsKycLevelPort ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tier this double holds, mirroring what <see cref="SetKycLevelAsync"/> pushed.
    ///
    /// <para>
    /// A customer never rated answers <c>null</c> as a SUCCESS, not as a failure: that is the
    /// distinction INT-21 depends on, since "the CBS says Simplified" and "the CBS has no opinion"
    /// lead to a divergence being reported in one case and not the other. A test can also set
    /// <see cref="KycLevels"/> directly to simulate the case the inference can never see — an
    /// officer changing the tier inside the CBS, with no command of ours behind it.
    /// </para>
    /// </summary>
    public Task<IntegrationResult<KycLevel?>> GetKycLevelAsync(
        ExternalId customerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<KycLevel?>(
            FakeAdapterOperations.ReadKycLevel, customerId.Value,
            () => IntegrationResult.Ok<KycLevel?>(
                KycLevels.TryGetValue(customerId.Value, out var level) ? level : null)));
    }

    public Task<IntegrationResult> SetKycLevelAsync(
        ExternalId id, KycLevel level, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(VoidWrite(
            FakeAdapterOperations.SetKycLevel, id.Value, key,
            () =>
            {
                if (!Customers.Contains(id.Value)) return NotFound("customer", id.Value);
                KycLevels[id.Value] = level;
                return IntegrationResult.Ok();
            }));
    }

    // ── ICbsAccountPort ─────────────────────────────────────────────────────────────────────

    public Task<IntegrationResult<ExternalId>> OpenAccountAsync(
        ExternalId customerId, string productCode, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Write<ExternalId>(
            FakeAdapterOperations.OpenAccount, customerId.Value, key,
            () =>
            {
                if (!Customers.Contains(customerId.Value))
                    return Propagate<ExternalId>(NotFound("customer", customerId.Value));

                if (!ProductCodes.Contains(productCode))
                    return Propagate<ExternalId>(Unmapped(MappingDomain.Product, productCode));

                var id = NextReference("ACC");
                AccountsByCustomer.TryAdd(customerId.Value, []);
                AccountsByCustomer[customerId.Value].Add(new CbsAccount(
                    new ExternalId(id),
                    AccountNumber: id,
                    ProductCode: productCode,
                    ProductLabel: productCode,
                    Currency: SeededCurrency,
                    Balance: 0m,
                    AvailableBalance: 0m,
                    Status: OperableAccountStatus,
                    OpenedOn: DateOnly.FromDateTime(FixedInstant.UtcDateTime)));

                return IntegrationResult.Ok(new ExternalId(id));
            },
            replay: existing => IntegrationResult.Ok(new ExternalId(existing))));
    }

    public Task<IntegrationResult<IReadOnlyList<CbsAccount>>> GetAccountsAsync(
        ExternalId customerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<IReadOnlyList<CbsAccount>>(
            FakeAdapterOperations.GetAccounts, customerId.Value,
            () => !Customers.Contains(customerId.Value)
                ? Propagate<IReadOnlyList<CbsAccount>>(NotFound("customer", customerId.Value))
                : IntegrationResult.Ok<IReadOnlyList<CbsAccount>>(
                    AccountsByCustomer.TryGetValue(customerId.Value, out var accounts)
                        ? [.. accounts]
                        : [])));
    }

    public Task<IntegrationResult<CbsBalance>> GetBalanceAsync(ExternalId accountId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<CbsBalance>(
            FakeAdapterOperations.GetBalance, accountId.Value,
            () =>
            {
                var account = FindAccount(accountId.Value);
                return account is null
                    ? Propagate<CbsBalance>(NotFound("account", accountId.Value))
                    : IntegrationResult.Ok(new CbsBalance(
                        accountId, account.Currency, account.Balance, account.AvailableBalance,
                        FixedInstant));
            }));
    }

    public Task<IntegrationResult<CbsDebitReceipt>> DebitAccountAsync(
        ExternalId accountId, decimal amount, string currency, string label,
        IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Write<CbsDebitReceipt>(
            FakeAdapterOperations.DebitAccount, accountId.Value, key,
            () =>
            {
                var account = FindAccount(accountId.Value);
                if (account is null)
                    return Propagate<CbsDebitReceipt>(NotFound("account", accountId.Value));

                // Both refusals are Functional: the far end answered and said no. Reading an
                // insufficient balance as transient would retry the debit until the salary lands,
                // which is not a decision an adapter gets to take.
                if (!string.Equals(account.Status, OperableAccountStatus, StringComparison.OrdinalIgnoreCase))
                    return IntegrationResult.Functional<CbsDebitReceipt>(
                        IntegrationErrors.AccountNotOperable,
                        $"Account {accountId.Value} is {account.Status}.");

                if (account.Balance < amount)
                    return IntegrationResult.Functional<CbsDebitReceipt>(
                        IntegrationErrors.InsufficientFunds,
                        $"Account {accountId.Value} holds less than the requested amount.");

                var reference = NextReference("DBT");
                MoveBalance(account, -amount);
                _debits[reference] = (accountId.Value, amount, currency);

                var receipt = new CbsDebitReceipt(accountId, reference, amount, currency, FixedInstant);
                LastDebitReceipt = receipt;
                return IntegrationResult.Ok(receipt);
            },
            replay: existing => ReplayedDebit(accountId, existing)));
    }

    public Task<IntegrationResult<CbsDebitReceipt>> ReverseDebitAsync(
        ExternalId accountId, string originalReference, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Write<CbsDebitReceipt>(
            FakeAdapterOperations.ReverseDebit, originalReference, key,
            () =>
            {
                // A reversal keyed on a reference the far end never issued is not an outage: the
                // caller is naming a movement that does not exist.
                if (!_debits.TryGetValue(originalReference, out var original))
                    return Propagate<CbsDebitReceipt>(NotFound("debit", originalReference));

                var account = FindAccount(original.AccountId);
                if (account is null)
                    return Propagate<CbsDebitReceipt>(NotFound("account", original.AccountId));

                var reference = NextReference("REV");
                MoveBalance(account, original.Amount);
                _debits[reference] = original;

                var receipt = new CbsDebitReceipt(
                    new ExternalId(original.AccountId), reference, original.Amount,
                    original.Currency, FixedInstant);
                LastDebitReceipt = receipt;
                return IntegrationResult.Ok(receipt);
            },
            replay: existing => ReplayedDebit(accountId, existing)));
    }

    // ── ICbsTransactionPort ─────────────────────────────────────────────────────────────────

    public Task<IntegrationResult<CbsPage<CbsTransaction>>> GetTransactionsAsync(
        ExternalId accountId, DateOnly from, DateOnly to, string? cursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<CbsPage<CbsTransaction>>(
            FakeAdapterOperations.GetTransactions, accountId.Value,
            () =>
            {
                LastTransactionCursor = cursor;

                if (FindAccount(accountId.Value) is null)
                    return Propagate<CbsPage<CbsTransaction>>(NotFound("account", accountId.Value));

                var offset = 0;
                // A cursor we did not issue is our caller's bug, not the far end's refusal — the
                // Technical family is the one that alerts rather than retries.
                if (cursor is not null
                    && !int.TryParse(cursor, CultureInfo.InvariantCulture, out offset))
                {
                    return IntegrationResult.Technical<CbsPage<CbsTransaction>>(
                        IntegrationErrors.PayloadInvalid, "The cursor was not issued by this adapter.");
                }

                var window = TransactionsByAccount.TryGetValue(accountId.Value, out var all)
                    ? all.Where(t => t.ValueDate >= from && t.ValueDate <= to).ToList()
                    : [];

                var page = window.Skip(offset).Take(Math.Max(1, TransactionPageSize)).ToList();
                var consumed = offset + page.Count;

                return IntegrationResult.Ok(new CbsPage<CbsTransaction>(
                    page,
                    consumed < window.Count ? consumed.ToString(CultureInfo.InvariantCulture) : null));
            }));
    }

    public Task<IntegrationResult<CbsMonthlyFlow>> GetMonthlyFlowAsync(
        ExternalId customerId, YearMonth month, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<CbsMonthlyFlow>(
            FakeAdapterOperations.GetMonthlyFlow, customerId.Value,
            () => !Customers.Contains(customerId.Value)
                ? Propagate<CbsMonthlyFlow>(NotFound("customer", customerId.Value))
                : IntegrationResult.Ok(new CbsMonthlyFlow(
                    month, MonthlyCreditTotal, MonthlyDebitTotal, SeededCurrency))));
    }

    // ── ICbsLoanPort ────────────────────────────────────────────────────────────────────────

    public Task<IntegrationResult<ExternalId>> SubmitLoanApplicationAsync(
        CbsLoanApplicationPayload payload, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);

        return Task.FromResult(Write<ExternalId>(
            FakeAdapterOperations.SubmitLoanApplication, payload.CustomerId.Value, key,
            () =>
            {
                if (!Customers.Contains(payload.CustomerId.Value))
                    return Propagate<ExternalId>(NotFound("customer", payload.CustomerId.Value));

                if (!ProductCodes.Contains(payload.ProductCode))
                    return Propagate<ExternalId>(Unmapped(MappingDomain.Product, payload.ProductCode));

                LastLoanApplication = payload;
                var id = NextReference("LN");

                LoansByCustomer.TryAdd(payload.CustomerId.Value, []);
                LoansByCustomer[payload.CustomerId.Value].Add(new CbsLoan(
                    new ExternalId(id),
                    payload.ProductCode,
                    payload.Amount,
                    payload.Amount,
                    payload.Currency,
                    Status: "PENDING",
                    DisbursedOn: null,
                    MaturityOn: null,
                    DaysInArrears: null));

                return IntegrationResult.Ok(new ExternalId(id));
            },
            replay: existing => IntegrationResult.Ok(new ExternalId(existing))));
    }

    public Task<IntegrationResult<IReadOnlyList<CbsLoan>>> GetLoansAsync(
        ExternalId customerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<IReadOnlyList<CbsLoan>>(
            FakeAdapterOperations.GetLoans, customerId.Value,
            () => !Customers.Contains(customerId.Value)
                ? Propagate<IReadOnlyList<CbsLoan>>(NotFound("customer", customerId.Value))
                : IntegrationResult.Ok<IReadOnlyList<CbsLoan>>(
                    LoansByCustomer.TryGetValue(customerId.Value, out var loans) ? [.. loans] : [])));
    }

    // ── IInsuranceProductPort ───────────────────────────────────────────────────────────────

    public Task<IntegrationResult<IReadOnlyList<string>>> GetProductCodesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<IReadOnlyList<string>>(
            FakeAdapterOperations.GetProductCodes, target: null,
            () => IntegrationResult.Ok<IReadOnlyList<string>>([.. InsurerProductCodes.Order()])));
    }

    public Task<IntegrationResult<InsuranceQuote>> PriceAsync(
        string insurerProductCode, Guid crmCustomerId, decimal? insuredAmount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<InsuranceQuote>(
            FakeAdapterOperations.Price, insurerProductCode,
            () => !InsurerProductCodes.Contains(insurerProductCode)
                ? Propagate<InsuranceQuote>(Unmapped(MappingDomain.Product, insurerProductCode))
                : IntegrationResult.Ok(new InsuranceQuote(
                    insurerProductCode,
                    insuredAmount is null ? QuotedPremium : insuredAmount.Value * PremiumRate,
                    SeededCurrency,
                    QuotedPeriodicity,
                    FixedInstant))));
    }

    public Task<IntegrationResult<InsuranceEligibility>> CheckEligibilityAsync(
        string insurerProductCode, Guid crmCustomerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<InsuranceEligibility>(
            FakeAdapterOperations.CheckEligibility, insurerProductCode,
            () => !InsurerProductCodes.Contains(insurerProductCode)
                ? Propagate<InsuranceEligibility>(Unmapped(MappingDomain.Product, insurerProductCode))
                : IntegrationResult.Ok(new InsuranceEligibility(
                    IsEligible, IsEligible ? [] : [.. IneligibilityReasons]))));
    }

    // ── IInsurancePolicyPort ────────────────────────────────────────────────────────────────

    public Task<IntegrationResult<ExternalId>> SubscribeAsync(
        InsurancePolicyPayload payload, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);

        return Task.FromResult(Write<ExternalId>(
            FakeAdapterOperations.Subscribe, payload.CrmCustomerId.ToString(), key,
            () =>
            {
                if (!InsurerProductCodes.Contains(payload.InsurerProductCode))
                    return Propagate<ExternalId>(
                        Unmapped(MappingDomain.Product, payload.InsurerProductCode));

                LastPolicyPayload = payload;
                var id = NextReference("POL");

                PoliciesByCustomer.TryAdd(PolicyHolder.Value, []);
                PoliciesByCustomer[PolicyHolder.Value].Add(new InsurancePolicy(
                    new ExternalId(id),
                    PolicyNumber: id,
                    payload.CrmCustomerId,
                    payload.InsurerProductCode,
                    // Pending and not Issued: an insurer acknowledges a subscription before it
                    // underwrites one, and a double that answered Issued would let a screen skip
                    // the state the real flow spends most of its time in (ASS-07).
                    Status: PolicyStatus.Pending,
                    payload.EffectiveDate,
                    ExpiryDate: null,
                    payload.PremiumAmount,
                    payload.Currency,
                    payload.Periodicity,
                    NextDueDate: payload.EffectiveDate));

                return IntegrationResult.Ok(new ExternalId(id));
            },
            replay: existing => IntegrationResult.Ok(new ExternalId(existing))));
    }

    public Task<IntegrationResult<IReadOnlyList<InsurancePolicy>>> GetPoliciesAsync(
        ExternalId customerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<IReadOnlyList<InsurancePolicy>>(
            FakeAdapterOperations.GetPolicies, customerId.Value,
            () => !Customers.Contains(customerId.Value)
                ? Propagate<IReadOnlyList<InsurancePolicy>>(NotFound("customer", customerId.Value))
                : IntegrationResult.Ok<IReadOnlyList<InsurancePolicy>>(
                    PoliciesByCustomer.TryGetValue(customerId.Value, out var policies)
                        ? [.. policies]
                        : [])));
    }

    public Task<IntegrationResult<InsurancePolicy>> GetPolicyAsync(
        ExternalId policyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<InsurancePolicy>(
            FakeAdapterOperations.GetPolicy, policyId.Value,
            () => FindPolicy(policyId.Value) is { } policy
                ? IntegrationResult.Ok(policy)
                : Propagate<InsurancePolicy>(NotFound("policy", policyId.Value))));
    }

    public Task<IntegrationResult<InsuranceCertificate>> GetCertificateAsync(
        ExternalId policyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<InsuranceCertificate>(
            FakeAdapterOperations.GetCertificate, policyId.Value,
            () => FindPolicy(policyId.Value) is { } policy
                ? IntegrationResult.Ok(new InsuranceCertificate(
                    policyId, CertificateContentType, [.. CertificateContent],
                    $"{policy.PolicyNumber}.pdf"))
                : Propagate<InsuranceCertificate>(NotFound("policy", policyId.Value))));
    }

    public Task<IntegrationResult> CancelAsync(
        ExternalId policyId, string reason, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(VoidWrite(
            FakeAdapterOperations.CancelPolicy, policyId.Value, key,
            () =>
            {
                var holder = PoliciesByCustomer
                    .FirstOrDefault(e => e.Value.Any(p => p.PolicyId.Value == policyId.Value));

                if (holder.Value is null) return NotFound("policy", policyId.Value);

                var index = holder.Value.FindIndex(p => p.PolicyId.Value == policyId.Value);
                holder.Value[index] = holder.Value[index] with { Status = PolicyStatus.Cancelled };
                return IntegrationResult.Ok();
            }));
    }

    // ── IInsuranceClaimPort ─────────────────────────────────────────────────────────────────

    public Task<IntegrationResult<ExternalId>> DeclareAsync(
        InsuranceClaimPayload payload, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(payload);

        return Task.FromResult(Write<ExternalId>(
            FakeAdapterOperations.DeclareClaim, payload.PolicyId.Value, key,
            () =>
            {
                if (FindPolicy(payload.PolicyId.Value) is null)
                    return Propagate<ExternalId>(NotFound("policy", payload.PolicyId.Value));

                LastClaimPayload = payload;
                var id = NextReference("CLM");

                ClaimsByPolicy.TryAdd(payload.PolicyId.Value, []);
                ClaimsByPolicy[payload.PolicyId.Value].Add(new InsuranceClaim(
                    new ExternalId(id),
                    ClaimNumber: id,
                    payload.PolicyId,
                    Status: ClaimStatus.Declared,
                    payload.OccurredOn,
                    DeclaredAt: FixedInstant,
                    IndemnityAmount: null,
                    MissingDocuments: null));

                return IntegrationResult.Ok(new ExternalId(id));
            },
            replay: existing => IntegrationResult.Ok(new ExternalId(existing))));
    }

    public Task<IntegrationResult<IReadOnlyList<InsuranceClaim>>> GetClaimsAsync(
        ExternalId policyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<IReadOnlyList<InsuranceClaim>>(
            FakeAdapterOperations.GetClaims, policyId.Value,
            () => FindPolicy(policyId.Value) is null
                ? Propagate<IReadOnlyList<InsuranceClaim>>(NotFound("policy", policyId.Value))
                : IntegrationResult.Ok<IReadOnlyList<InsuranceClaim>>(
                    ClaimsByPolicy.TryGetValue(policyId.Value, out var claims) ? [.. claims] : [])));
    }

    public Task<IntegrationResult<InsuranceClaim>> GetClaimAsync(
        ExternalId claimId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Read<InsuranceClaim>(
            FakeAdapterOperations.GetClaim, claimId.Value,
            () => FindClaim(claimId.Value) is { } claim
                ? IntegrationResult.Ok(claim)
                : Propagate<InsuranceClaim>(NotFound("claim", claimId.Value))));
    }

    public Task<IntegrationResult> AddDocumentAsync(
        ExternalId claimId, string storageRef, IdempotencyKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(VoidWrite(
            FakeAdapterOperations.AddClaimDocument, claimId.Value, key,
            () =>
            {
                if (FindClaim(claimId.Value) is null) return NotFound("claim", claimId.Value);
                AddedClaimDocuments.Add(storageRef);
                return IntegrationResult.Ok();
            }));
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The write template: forced outcome first, then the replay, then the real thing. The order
    /// matters — a replay is answered from the store WITHOUT recording a call, and that silence
    /// in <see cref="Calls"/> is the only observable difference between an idempotent adapter and
    /// one that writes twice.
    /// </summary>
    private IntegrationResult<T> Write<T>(
        string operation,
        string? target,
        IdempotencyKey key,
        Func<IntegrationResult<T>> write,
        Func<string, IntegrationResult<T>> replay)
    {
        if (Forced(isWrite: true) is { } forced)
        {
            // Recorded even though it failed: the attempt left this process, and a test counting
            // attempts against a refusing far end needs to see it.
            Record(operation, target, key);
            return Propagate<T>(forced);
        }

        if (_answeredWrites.TryGetValue(StoreKey(operation, key), out var existing))
            return replay(existing);

        Record(operation, target, key);

        var result = write();

        // Only a SUCCESS is remembered. A functional refusal must be re-answered on replay, not
        // turned into a cached identifier the dispatcher would read as a completed write.
        if (result.IsSuccess) _answeredWrites[StoreKey(operation, key)] = ReferenceOf(result.Value);

        return result;
    }

    /// <summary>Same, for a write that returns no value: the store only remembers it happened.</summary>
    private IntegrationResult VoidWrite(
        string operation, string? target, IdempotencyKey key, Func<IntegrationResult> write)
    {
        if (Forced(isWrite: true) is { } forced)
        {
            Record(operation, target, key);
            return forced;
        }

        if (_answeredWrites.ContainsKey(StoreKey(operation, key))) return IntegrationResult.Ok();

        Record(operation, target, key);

        var result = write();
        if (result.IsSuccess) _answeredWrites[StoreKey(operation, key)] = string.Empty;

        return result;
    }

    private IntegrationResult<T> Read<T>(
        string operation, string? target, Func<IntegrationResult<T>> read)
    {
        Record(operation, target, key: null);

        return Forced(isWrite: false) is { } forced ? Propagate<T>(forced) : read();
    }

    /// <summary>
    /// The failure in force for this call, or null. The side-specific knob wins so a duplicate can
    /// refuse the writes while the reads keep answering — the shape the rejection queue is
    /// inspected in.
    /// </summary>
    private IntegrationResult? Forced(bool isWrite)
    {
        var candidate = (isWrite ? ForcedWriteOutcome : ForcedReadOutcome) ?? ForcedOutcome;
        return candidate is { IsFailure: true } ? candidate : null;
    }

    private void Record(string operation, string? target, IdempotencyKey? key)
    {
        Calls.Add(new FakeAdapterCall(operation, target, key?.Value));
        if (key is not null) LastIdempotencyKey = key;
    }

    private static string StoreKey(string operation, IdempotencyKey key)
        => $"{operation}|{key.Value}";

    /// <summary>
    /// What a replay must hand back. Both write shapes produce one string — an external id or a
    /// debit reference — so the store stays a single dictionary instead of one per return type.
    /// </summary>
    private static string ReferenceOf<T>(T value) => value switch
    {
        ExternalId id => id.Value,
        CbsDebitReceipt receipt => receipt.Reference,
        _ => string.Empty,
    };

    private IntegrationResult<CbsDebitReceipt> ReplayedDebit(ExternalId accountId, string reference)
        => _debits.TryGetValue(reference, out var original)
            ? IntegrationResult.Ok(new CbsDebitReceipt(
                new ExternalId(original.AccountId), reference, original.Amount,
                original.Currency, FixedInstant))
            : Propagate<CbsDebitReceipt>(NotFound("debit", reference));

    /// <summary>
    /// Re-wraps a failure under another value type, keeping the family. Written once because
    /// losing the family in a conversion is exactly the bug <see cref="ErrorFamily"/> exists to
    /// prevent: a transient failure re-raised as functional stops being retried.
    /// </summary>
    private static IntegrationResult<T> Propagate<T>(IntegrationResult failure) => failure.Family switch
    {
        ErrorFamily.Transient => IntegrationResult.Transient<T>(failure.Code!, failure.Detail),
        ErrorFamily.Functional => IntegrationResult.Functional<T>(failure.Code!, failure.Detail),
        _ => IntegrationResult.Technical<T>(failure.Code!, failure.Detail),
    };

    /// <summary>
    /// An entity the far end does not hold. Functional and not Technical: the external system
    /// answered, and retrying would get the same answer.
    /// </summary>
    private static IntegrationResult NotFound(string what, string id)
        => IntegrationResult.Functional(
            IntegrationErrors.ExternalEntityNotFound,
            $"No {what} with reference '{id}' in the external system.");

    /// <summary>
    /// A code that resolves to nothing. Technical, and the detail NAMES the domain and the code:
    /// an administrator told only "mapping missing" has a table of eight domains to search.
    /// </summary>
    private static IntegrationResult Unmapped(MappingDomain domain, string code)
        => IntegrationResult.Technical(
            IntegrationErrors.MappingMissing,
            $"No {domain} mapping for CRM code '{code}'.");

    private CbsAccount? FindAccount(string accountId)
        => AccountsByCustomer.Values.SelectMany(a => a).FirstOrDefault(a => a.AccountId.Value == accountId);

    private InsurancePolicy? FindPolicy(string policyId)
        => PoliciesByCustomer.Values.SelectMany(p => p).FirstOrDefault(p => p.PolicyId.Value == policyId);

    private InsuranceClaim? FindClaim(string claimId)
        => ClaimsByPolicy.Values.SelectMany(c => c).FirstOrDefault(c => c.ClaimId.Value == claimId);

    /// <summary>
    /// Applies a movement to an account. <see cref="CbsAccount"/> is a record, so the list entry
    /// is replaced rather than mutated — and the available balance follows the ledger one, or a
    /// debited account would keep reporting the money as spendable.
    /// </summary>
    private void MoveBalance(CbsAccount account, decimal delta)
    {
        foreach (var accounts in AccountsByCustomer.Values)
        {
            var index = accounts.FindIndex(a => a.AccountId.Value == account.AccountId.Value);
            if (index < 0) continue;

            var current = accounts[index];
            accounts[index] = current with
            {
                Balance = current.Balance + delta,
                AvailableBalance = current.AvailableBalance is null
                    ? null
                    : current.AvailableBalance + delta,
            };
            return;
        }
    }

    /// <summary>
    /// The next generated reference. One shared sequence so no two families can mint the same id,
    /// and the number is burned only by a real write — a replay reuses the stored one.
    /// </summary>
    private string NextReference(string prefix)
        => $"{prefix}-{++_sequence:D4}";
}
