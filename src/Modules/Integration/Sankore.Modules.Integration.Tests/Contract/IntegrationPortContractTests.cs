namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-10. One suite, run against every adapter of either family.
///
/// <para>
/// It exists because the adapters are substitutable by design and the platform above them is
/// not written per product: the dispatcher decides to retry from <see cref="ErrorFamily"/>
/// alone, the rejection queue is filled from it, and the command lifecycle treats an
/// <see cref="ExternalId"/> coming back twice as proof the write happened once. Every handler
/// and every job is therefore tested against the fake. The moment Temenos, ORASS and the fake
/// disagree about what "absent", "duplicate" or "timed out" means, those tests stop saying
/// anything about production — and nothing would report it. So the contract is asserted ONCE
/// and inherited, not restated per adapter, and a new adapter's whole obligation is to subclass
/// it.
/// </para>
///
/// <para>
/// Generic in the port rather than one flat class per implementation: the seven port groups
/// share these two facts exactly, and the derived suite still names its own port type
/// (<c>CbsCustomerPortContractTests : IntegrationPortContractTests&lt;ICbsCustomerPort&gt;</c>),
/// so a subclass sees <c>CreatePort()</c> returning the interface it implements and nothing
/// wider.
/// </para>
/// </summary>
public abstract class IntegrationPortContractTests<TPort> where TPort : class
{
    /// <summary>A healthy adapter, with whatever portfolio its implementation seeds.</summary>
    protected abstract TPort CreatePort();

    /// <summary>
    /// The same adapter, with the far end not answering. A separate instance rather than a knob
    /// because a real adapter is configured once and cannot be switched mid-flight.
    /// </summary>
    protected abstract TPort CreateTimingOutPort();

    /// <summary>
    /// Any call whose result carries a value, reduced to the result and a closure that reads its
    /// <c>Value</c>. Two pieces because <see cref="IntegrationResult{T}"/> is generic and this
    /// suite cannot name T — and the Value-access rule is exactly what must not be restated per
    /// adapter.
    /// </summary>
    protected abstract Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(TPort port);

    /// <summary>
    /// An external reference no back-office holds. Overridable because a real system may reject
    /// a malformed reference before looking it up, and the fact being asserted is about an
    /// absent entity, not about a syntax check.
    /// </summary>
    protected virtual ExternalId AbsentId => new("SANKORE-CONTRACT-ABSENT-ENTITY");

    /// <summary>A product code that resolves to nothing in any mapping table.</summary>
    protected virtual string UnknownProductCode => "SANKORE-CONTRACT-UNKNOWN-PRODUCT";

    /// <summary>
    /// How many writes the implementation actually performed, when it can tell. The in-memory
    /// double counts them; an HTTP adapter cannot, and returns null — the idempotency fact then
    /// asserts only the half the dispatcher depends on, the same external id coming back.
    /// </summary>
    protected virtual int? WriteCount(TPort port) => null;

    protected static IdempotencyKey Key(string scenario) => new($"contract-{scenario}");

    [Fact]
    public async Task A_timeout_should_be_transient_so_the_dispatcher_retries_it()
    {
        var (result, _) = await AnyValuedCallAsync(CreateTimingOutPort());

        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(ErrorFamily.Transient,
            "a timeout leaves us unable to tell 'not written' from 'written, answer lost' — the " +
            "only safe reading is 'try again', and the idempotency key is what makes that safe");
        result.IsRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task Value_of_a_failed_result_should_throw_rather_than_hand_back_a_default()
    {
        var (result, readValue) = await AnyValuedCallAsync(CreateTimingOutPort());

        result.IsFailure.Should().BeTrue();

        // A default ExternalId here would be written into integration_reference as the empty
        // string and read back later as a real reference to nothing. Failing loudly is the only
        // behaviour a caller that forgot to check IsSuccess can survive.
        readValue.Should().Throw<InvalidOperationException>();
    }

    // ── Assertions shared by the derived suites ─────────────────────────────────────────────

    /// <summary>
    /// The far end does not hold the entity. Functional, so the dispatcher parks the command for
    /// a human instead of retrying a lookup that will keep answering the same thing — and an
    /// answer, never an exception: an exception would bubble out of a Hangfire job as a crash and
    /// lose the reason.
    /// </summary>
    protected static void ShouldBeAbsentEntity(IntegrationResult result)
    {
        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.ExternalEntityNotFound);
        result.IsRetryable.Should().BeFalse("retrying a lookup of something absent changes nothing");
    }

    /// <summary>
    /// The far end already holds the record. Functional and NOT transient: the duplicate is the
    /// system's considered answer, and retrying it would refuse again until a human looks.
    /// </summary>
    protected static void ShouldBeDuplicate(IntegrationResult result)
    {
        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.Duplicate);
        result.IsRetryable.Should().BeFalse();
    }

    /// <summary>
    /// A code that resolves to nothing. Technical, because the misconfiguration is OURS: no
    /// amount of retrying fills a mapping table, and the family is what decides whether an
    /// administrator is woken or a clerk is handed a rejection.
    ///
    /// <para>
    /// Either code is accepted. <see cref="IntegrationErrors.MappingMissing"/> is the one an
    /// adapter raises when the translation is absent on our side;
    /// <see cref="IntegrationErrors.UnknownProduct"/> when it asked and the far end disowned the
    /// product. This fact only exercises a code nothing maps, so both are correct answers to it —
    /// what must not vary is the family and the detail naming the code.
    /// </para>
    /// </summary>
    protected static void ShouldBeUnmappedCode(IntegrationResult result, string code)
    {
        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().BeOneOf(IntegrationErrors.MappingMissing, IntegrationErrors.UnknownProduct);
        result.Detail.Should().NotBeNullOrWhiteSpace();
        result.Detail.Should().Contain(code,
            "an administrator told only 'mapping missing' has eight domains to search");
    }
}

/// <summary>
/// A port that addresses entities the far end owns — six of the seven.
///
/// <para>
/// Split from the base rather than merged into it because <c>IInsuranceProductPort</c> addresses
/// no entity at all: its three methods take a product code, and an unknown code is a
/// <see cref="ErrorFamily.Technical"/> mapping failure, not a missing record. Folding this fact
/// into the base would have forced that suite to invent an answer for a question its port cannot
/// be asked.
/// </para>
/// </summary>
public abstract class IntegrationEntityPortContractTests<TPort> : IntegrationPortContractTests<TPort>
    where TPort : class
{
    /// <summary>
    /// Any call naming <see cref="IntegrationPortContractTests{TPort}.AbsentId"/>. A read where
    /// the port has one, a write where it does not — the rule under test is about the answer, not
    /// about the verb.
    /// </summary>
    protected abstract Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(TPort port);

    [Fact]
    public async Task An_absent_external_entity_should_be_a_functional_failure_not_an_exception()
    {
        ShouldBeAbsentEntity(await CallAgainstAnAbsentEntityAsync(CreatePort()));
    }
}

/// <summary>
/// A port that writes — five of the seven. <c>ICbsTransactionPort</c> and
/// <c>IInsuranceProductPort</c> only read, so neither can be asked what it does with a replayed
/// idempotency key.
/// </summary>
public abstract class IntegrationWritePortContractTests<TPort> : IntegrationEntityPortContractTests<TPort>
    where TPort : class
{
    /// <summary>
    /// The same adapter, with the far end refusing every write as already held. A separate
    /// instance for the same reason the timing-out one is.
    /// </summary>
    protected abstract TPort CreateDuplicatingPort();

    /// <summary>
    /// The port's cheapest creating write, under the caller's key. Returns the reference the far
    /// end minted, which is the value the whole idempotency contract is about.
    /// </summary>
    protected abstract Task<IntegrationResult<ExternalId>> CreatingWriteAsync(
        TPort port, IdempotencyKey key);

    [Fact]
    public async Task The_same_idempotency_key_twice_should_return_one_external_id_and_write_once()
    {
        var sut = CreatePort();
        var key = Key("idempotency");

        var first = await CreatingWriteAsync(sut, key);
        var second = await CreatingWriteAsync(sut, key);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Should().Be(first.Value,
            "the dispatcher treats the reference coming back as proof the write happened once; a " +
            "second, different id is a second record on the far end that nothing will ever reconcile");

        // Null when the implementation cannot count its own writes (every HTTP adapter). The
        // assertion above is the half the dispatcher actually depends on; this one is the half
        // that proves the far end was not touched twice, and is asserted wherever it is knowable.
        if (WriteCount(sut) is { } writes)
        {
            writes.Should().Be(1, "the replay must be answered from the key, not sent again");
        }
    }

    [Fact]
    public async Task A_different_idempotency_key_should_be_a_second_write()
    {
        var sut = CreatePort();

        var first = await CreatingWriteAsync(sut, Key("distinct-a"));
        var second = await CreatingWriteAsync(sut, Key("distinct-b"));

        // The mirror of the fact above, and the reason it cannot be implemented by ignoring the
        // key: two genuinely different requests must still produce two records.
        second.Value.Should().NotBe(first.Value);
    }

    [Fact]
    public async Task A_duplicate_should_be_functional_so_the_dispatcher_does_not_retry_it()
    {
        ShouldBeDuplicate(await CreatingWriteAsync(CreateDuplicatingPort(), Key("duplicate")));
    }
}
