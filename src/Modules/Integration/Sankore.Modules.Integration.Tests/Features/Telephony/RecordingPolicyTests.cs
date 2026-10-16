namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Recordings;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The recording policy (TEL-B-15) — the gate the whole of Lot 5 sits behind.
/// </summary>
public sealed class RecordingPolicyTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public RecordingPolicyTests() => _factory = new TestIntegrationDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_tenant_that_configured_nothing_keeps_nothing()
    {
        await using var db = _factory.CreateContext();

        var policy = await new TelephonyRecordingPolicyReader(db)
            .ReadAsync(_tenant, CancellationToken.None);

        // The whole shape of "built but shipped disabled": an absent row is the correct and safest
        // state, which is why there is no seeder for this one referential.
        policy.Mode.Should().Be(TelRecordingMode.Disabled);
        policy.RetentionDays.Should().Be(TelRecordingPolicy.DefaultRetentionDays);
        policy.MaxDurationSeconds.Should().Be(TelRecordingPolicy.DefaultMaxDurationSeconds);
    }

    [Theory]
    [InlineData(TelRecordingMode.AllCalls)]
    [InlineData(TelRecordingMode.OnDemand)]
    public async Task Switching_recording_on_without_a_legal_basis_is_refused(TelRecordingMode mode)
    {
        // The one rule the aggregate exists to hold: a recording kept without a stated basis is a
        // file nobody can justify holding.
        var result = await ApplyAsync(mode, legalBasis: null, purpose: "Qualité de service");

        result.Error.Should().Be(TelephonyErrors.RecordingLegalBasisRequired);

        await using var db = _factory.CreateContext();
        db.TelRecordingPolicies.Should().BeEmpty("nothing is written for a refused policy");
    }

    [Fact]
    public async Task A_basis_without_a_purpose_is_refused_too()
    {
        // Different questions — "we may" and "we need to" — and a purpose limitation nobody wrote
        // down is one nobody can be held to.
        var result = await ApplyAsync(
            TelRecordingMode.AllCalls, legalBasis: "Obligation légale CIMA", purpose: "  ");

        result.Error.Should().Be(TelephonyErrors.RecordingPurposeRequired);
    }

    [Fact]
    public async Task Disabled_needs_neither_and_can_always_be_set()
    {
        // An institution switching the feature OFF must never be blocked by a validation about
        // why it was on.
        var result = await ApplyAsync(TelRecordingMode.Disabled, null, null);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Switching_back_to_Disabled_keeps_the_words_that_explain_the_files_already_held()
    {
        await ApplyAsync(
            TelRecordingMode.AllCalls, "Obligation légale CIMA", "Preuve de consentement");

        var result = await ApplyAsync(TelRecordingMode.Disabled, null, null);

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var policy = db.TelRecordingPolicies.Single();

        policy.Mode.Should().Be(TelRecordingMode.Disabled);

        // The recordings already taken are still held under them; blanking the words would leave
        // those files unexplained.
        policy.LegalBasis.Should().Be("Obligation légale CIMA");
        policy.Purpose.Should().Be("Preuve de consentement");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task A_non_positive_retention_is_refused(int days)
    {
        // The mistake a settings typo produces, and the one M13's lead retention documents as
        // irreversible if honoured.
        var result = await ApplyAsync(
            TelRecordingMode.AllCalls, "Base", "Finalité", retentionDays: days);

        result.Error.Should().Be(TelephonyErrors.RecordingRetentionInvalid);
    }

    [Fact]
    public async Task A_non_positive_duration_ceiling_is_refused()
    {
        var result = await ApplyAsync(
            TelRecordingMode.AllCalls, "Base", "Finalité", maxDurationSeconds: 0);

        result.Error.Should().Be(TelephonyErrors.RecordingDurationCeilingInvalid);
    }

    // ── What the mode MEANS ───────────────────────────────────────────────────

    [Fact]
    public void Disabled_keeps_nothing_even_when_the_agent_asked()
    {
        var policy = TelRecordingPolicy.DisabledFor(_tenant);

        var asked = Call();
        asked.MarkForRecording();

        // The institution has not stated a basis. An agent's request cannot create one.
        policy.Keeps(asked).Should().BeFalse();
    }

    [Fact]
    public async Task AllCalls_keeps_a_call_nobody_asked_about()
    {
        var policy = await ConfiguredAsync(TelRecordingMode.AllCalls);

        policy.Keeps(Call()).Should().BeTrue();
    }

    [Fact]
    public async Task OnDemand_keeps_only_what_an_agent_asked_for()
    {
        var policy = await ConfiguredAsync(TelRecordingMode.OnDemand);

        policy.Keeps(Call()).Should().BeFalse();

        var asked = Call();
        asked.MarkForRecording();

        // The mode for a basis of CONSENT: consent is given per conversation, and the agent is the
        // only one who knows it was.
        policy.Keeps(asked).Should().BeTrue();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private TelCall Call()
    {
        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect("+22507111111");
        var to = protector.Protect("+22507000001");

        return TelCall.Open(
            _tenant, Guid.NewGuid(), $"prov-{Guid.NewGuid()}",
            TelCallDirection.Inbound, TelCallStatus.Completed,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            TelephonyTestHarness.Now);
    }

    private async Task<TelRecordingPolicy> ConfiguredAsync(TelRecordingMode mode)
    {
        (await ApplyAsync(mode, "Obligation légale CIMA", "Preuve de consentement"))
            .IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();

        return await new TelephonyRecordingPolicyReader(db)
            .ReadAsync(_tenant, CancellationToken.None);
    }

    private async Task<Result> ApplyAsync(
        TelRecordingMode mode,
        string? legalBasis,
        string? purpose,
        int retentionDays = 90,
        int maxDurationSeconds = 3600)
    {
        await using var db = _factory.CreateContext();

        return await new SetRecordingPolicyHandler(
                db, TelephonyTestHarness.User(_tenant), TelephonyTestHarness.Clock)
            .Handle(
                new SetRecordingPolicyCommand(
                    mode, legalBasis, purpose, retentionDays,
                    DeleteAtProvider: false, maxDurationSeconds),
                CancellationToken.None);
    }
}
