namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Contract;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The Temenos adapter, run against the shared port contracts over the stub transport (INT-10).
///
/// <para>
/// Inheriting these suites is the point, and it is why they were written abstract: the platform
/// above an adapter is not written per product — the dispatcher decides to retry from
/// <see cref="ErrorFamily"/> alone, the rejection queue is filled from it, and the command
/// lifecycle treats an <see cref="ExternalId"/> coming back twice as proof the write happened
/// once. The moment Temenos and the in-memory double disagree about what "absent", "duplicate" or
/// "timed out" means, every handler test in this module stops saying anything about production,
/// and nothing would report it. So these classes are wiring and contain no facts of their own.
/// </para>
///
/// <para>
/// Kept in one file, as the Fake's derivations are: each class is a handful of lines, and
/// splitting them would hide the one thing worth seeing at a glance — which contracts this
/// adapter is held to, and which one it is not (see <c>TemenosCbsAccountPortTests</c>).
/// </para>
/// </summary>
internal sealed class TemenosHarnessPool : IDisposable
{
    private readonly List<TemenosTestHarness> _harnesses = [];

    /// <summary>
    /// Keyed by port instance, because the contract suite's <c>WriteCount</c> hook is handed a
    /// port and nothing else — and what has to be counted lives on the stub behind it. Reference
    /// equality, since the adapter is a class with no value semantics to accidentally collide on.
    /// </summary>
    private readonly Dictionary<object, TemenosTestHarness> _byPort = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A fresh adapter over a fresh stub installation. Fresh because the stub keeps what a flow
    /// wrote — the party it just created, the account it just opened — so sharing one between
    /// facts would let one test change what the next one reads.
    /// </summary>
    public TemenosTestHarness New(StubMode mode = StubMode.Healthy)
    {
        var harness = TemenosTestHarness.Create();
        harness.Transport.Mode = mode;

        _harnesses.Add(harness);
        _byPort[harness.Adapter] = harness;

        return harness;
    }

    public TemenosTestHarness Of(object port) => _byPort[port];

    public void Dispose()
    {
        foreach (var harness in _harnesses) harness.Dispose();
    }
}

/// <summary>
/// <c>ICbsCustomerPort</c>, held to the whole write contract (INT-12).
///
/// <para>
/// <b><see cref="CreatingWriteAsync"/> derives the payload from the key, and that is faithful
/// rather than convenient.</b> An <see cref="IdempotencyKey"/> is documented as deterministic and
/// derived from what the write IS, so one CRM customer always produces one key and two different
/// keys are, by construction, two different customers. For an HTTP adapter the idempotency of a
/// creation is realised by the CRM reference — INT-12's pre-create search — and not by a keyed
/// store, so a suite that sent ONE customer under TWO keys would be asking this adapter to create
/// a second party for a person who already has one. Which is exactly the duplicate the criterion
/// exists to prevent, and the adapter correctly refuses to do it.
/// </para>
/// </summary>
public sealed class TemenosCbsCustomerPortTests : CbsCustomerPortContractTests, IDisposable
{
    private readonly TemenosHarnessPool _pool = new();

    protected override ICbsCustomerPort CreatePort() => _pool.New().Adapter;

    protected override ICbsCustomerPort CreateTimingOutPort()
        => _pool.New(StubMode.TimingOut).Adapter;

    protected override ICbsCustomerPort CreateDuplicatingPort()
        => _pool.New(StubMode.Duplicating).Adapter;

    protected override CbsCustomerPayload Payload => PayloadFor(Key("default"));

    protected override Task<IntegrationResult<ExternalId>> CreatingWriteAsync(
        ICbsCustomerPort port, IdempotencyKey key)
        => port.CreateCustomerAsync(PayloadFor(key), key, CancellationToken.None);

    /// <summary>
    /// Unlike the in-memory double, this adapter CAN be asked how many creations reached the far
    /// end: the pre-create search is what prevents the second one, so a replay that reached a
    /// <c>POST</c> is a visible failure rather than an unknowable.
    /// </summary>
    protected override int? WriteCount(ICbsCustomerPort port) => _pool.Of(port).Transport.CustomerPosts;

    /// <summary>
    /// One CRM customer per idempotency key. The code fields are left as the shared fixture has
    /// them, so the eight mapping domains of INT-04 are genuinely translated on every creation.
    /// </summary>
    internal static CbsCustomerPayload PayloadFor(IdempotencyKey key) =>
        FakeAdapterFixtures.CustomerPayload() with
        {
            CrmCustomerId = DeterministicGuid(key.Value),
            CrmReference = $"CRM-{key.Value}",
        };

    /// <summary>
    /// A Guid from a string, so one key yields one CRM customer on every run and on every
    /// machine. The hash is not a security choice — it is the shortest stable way to get sixteen
    /// bytes out of a name — but it is SHA-256 rather than MD5 because the repository's analysers
    /// refuse the latter, and a suppression would be a worse thing to read than a longer hash.
    /// </summary>
    private static Guid DeterministicGuid(string value)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    public void Dispose() => _pool.Dispose();
}

/// <summary>
/// <c>ICbsTransactionPort</c>, held to the whole read contract (INT-13, INT-22).
/// </summary>
public sealed class TemenosCbsTransactionPortTests : CbsTransactionPortContractTests, IDisposable
{
    private readonly TemenosHarnessPool _pool = new();

    protected override ICbsTransactionPort CreatePort() => _pool.New().Adapter;

    protected override ICbsTransactionPort CreateTimingOutPort()
        => _pool.New(StubMode.TimingOut).Adapter;

    protected override ExternalId ExistingCustomerId => new(TemenosFixtures.SeededCustomerId);

    protected override ExternalId ExistingAccountId => new(TemenosFixtures.SeededAccountId);

    public void Dispose() => _pool.Dispose();
}

/// <summary>
/// <c>ICbsAccountPort</c> — and the one place this adapter is NOT held to the suite written for
/// its port.
///
/// <para>
/// <b>Why this derives from <c>IntegrationWritePortContractTests</c> and not from
/// <c>CbsAccountPortContractTests</c>.</b> Three of that suite's facts are about the premium
/// debit: an over-draw must be functional, a debit replayed under one key must post once, and a
/// reversal of a reference the far end never issued must be a functional not-found. All three
/// require a working <c>DebitAccountAsync</c>, which belongs to ASS-05 and is deliberately NOT
/// implemented here — INT-13's criteria do not include it, and a plausible implementation written
/// without the criteria that govern it would be a method that moves money. The adapter therefore
/// answers <c>CapabilityNotSupported</c> and leaves the two capabilities out of its matrix, which
/// those three facts cannot accept.
/// </para>
///
/// <para>
/// So everything in the suite that is NOT about debit is inherited from the tier above
/// (idempotency, the duplicate, the absent entity, the timeout, the Value rule), and the four
/// account facts of <c>CbsAccountPortContractTests</c> that INT-13 does cover are restated below
/// — verbatim in substance, with the same reasons. <b>The day ASS-05 implements the debit, this
/// class should derive from <c>CbsAccountPortContractTests</c> and the four restated facts should
/// be deleted</b>; a comment is left on each saying so.
/// </para>
/// </summary>
public sealed class TemenosCbsAccountPortTests : IntegrationWritePortContractTests<ICbsAccountPort>, IDisposable
{
    private readonly TemenosHarnessPool _pool = new();

    private static ExternalId ExistingCustomerId => new(TemenosFixtures.SeededCustomerId);

    private static ExternalId ExistingAccountId => new(TemenosFixtures.SeededAccountId);

    private static string ExistingProductCode => TemenosFixtures.SeededCrmProductCode;

    protected override ICbsAccountPort CreatePort() => _pool.New().Adapter;

    protected override ICbsAccountPort CreateTimingOutPort()
        => _pool.New(StubMode.TimingOut).Adapter;

    protected override ICbsAccountPort CreateDuplicatingPort()
        => _pool.New(StubMode.Duplicating).Adapter;

    protected override Task<IntegrationResult<ExternalId>> CreatingWriteAsync(
        ICbsAccountPort port, IdempotencyKey key)
        => port.OpenAccountAsync(ExistingCustomerId, ExistingProductCode, key, CancellationToken.None);

    protected override async Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(ICbsAccountPort port)
        => await port.GetBalanceAsync(AbsentId, CancellationToken.None);

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        ICbsAccountPort port)
    {
        var result = await port.GetBalanceAsync(ExistingAccountId, CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    // ── Restated from CbsAccountPortContractTests — delete when this class can derive from it ──

    /// <summary>INT-13, criterion 2. Verbatim from <c>CbsAccountPortContractTests</c>.</summary>
    [Fact]
    public async Task An_unresolvable_product_code_should_be_a_technical_failure_naming_it()
    {
        var sut = CreatePort();

        var result = await sut.OpenAccountAsync(
            ExistingCustomerId, UnknownProductCode, Key("unknown-product"), CancellationToken.None);

        ShouldBeUnmappedCode(result, UnknownProductCode);
    }

    /// <summary>Verbatim from <c>CbsAccountPortContractTests</c>.</summary>
    [Fact]
    public async Task Opening_an_account_for_an_absent_customer_should_be_functional_not_found()
    {
        var sut = CreatePort();

        var result = await sut.OpenAccountAsync(
            AbsentId, ExistingProductCode, Key("absent-holder"), CancellationToken.None);

        ShouldBeAbsentEntity(result);
    }

    /// <summary>Verbatim from <c>CbsAccountPortContractTests</c>.</summary>
    [Fact]
    public async Task A_newly_opened_account_should_be_listed_and_readable_under_its_reference()
    {
        var sut = CreatePort();

        var opened = await sut.OpenAccountAsync(
            ExistingCustomerId, ExistingProductCode, Key("open-round-trip"), CancellationToken.None);
        opened.IsSuccess.Should().BeTrue();

        var accounts = await sut.GetAccountsAsync(ExistingCustomerId, CancellationToken.None);
        var balance = await sut.GetBalanceAsync(opened.Value, CancellationToken.None);

        accounts.IsSuccess.Should().BeTrue();
        accounts.Value.Select(a => a.AccountId).Should().Contain(opened.Value,
            "Customer 360 lists accounts from the customer, so one that opened but does not list "
            + "is invisible to the counter that just created it");
        balance.IsSuccess.Should().BeTrue();
        balance.Value.AccountId.Should().Be(opened.Value);
    }

    /// <summary>Verbatim from <c>CbsAccountPortContractTests</c>.</summary>
    [Fact]
    public async Task A_read_balance_should_say_when_it_was_true()
    {
        var sut = CreatePort();

        var balance = await sut.GetBalanceAsync(ExistingAccountId, CancellationToken.None);

        balance.IsSuccess.Should().BeTrue();
        balance.Value.AsOf.Should().NotBe(default(DateTimeOffset),
            "a figure shown at a counter without an instant cannot be told apart from a stale one");
        balance.Value.Currency.Should().NotBeNullOrWhiteSpace();
    }

    public void Dispose() => _pool.Dispose();
}
