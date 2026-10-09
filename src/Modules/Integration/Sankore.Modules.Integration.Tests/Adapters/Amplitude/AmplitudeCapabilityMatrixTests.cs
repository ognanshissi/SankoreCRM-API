namespace Sankore.Modules.Integration.Tests.Adapters.Amplitude;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Amplitude;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-31, criterion 3 — « la matrice de capacités est calculée selon la version ».
///
/// <para>
/// The deliverable half of INT-31, and the one a screen depends on: a front end reads this matrix
/// to decide which buttons exist at all, so a capability declared in the wrong mode is a button
/// that waits for an answer nothing will send, and one declared that the installation cannot serve
/// is a button that fails in front of a client.
/// </para>
/// </summary>
public sealed class AmplitudeCapabilityMatrixTests
{
    private static IntegrationCapabilities Matrix(
        AmplitudeVersion version, IntegrationMode mode = IntegrationMode.Batch)
        => AmplitudeCapabilityMatrix.For(
            new AmplitudeSettings { AmplitudeVersion = version }, mode);

    [Fact]
    public void An_up_installation_serves_its_writes_live()
    {
        var matrix = Matrix(AmplitudeVersion.Up, IntegrationMode.Api);

        foreach (var write in AmplitudeCapabilityMatrix.Writes)
        {
            matrix.Supports(write).Should().BeTrue($"{write} is an Amplitude Up API service");
            matrix.ModeOf(write).Should().Be(CapabilityMode.RealTime);
        }
    }

    [Fact]
    public void A_pre_up_installation_serves_the_same_writes_through_the_batch_socle()
    {
        var matrix = Matrix(AmplitudeVersion.Legacy);

        // The list is identical and only the mode changes — the claim criterion 3 makes. A release
        // that dropped operations would be a product difference; this is a carrier difference.
        foreach (var write in AmplitudeCapabilityMatrix.Writes)
        {
            matrix.Supports(write).Should().BeTrue($"{write} travels in the outbound file");
            matrix.ModeOf(write).Should().Be(CapabilityMode.Batch);
        }
    }

    [Fact]
    public void The_two_releases_declare_exactly_the_same_write_operations()
    {
        var up = Matrix(AmplitudeVersion.Up, IntegrationMode.Api).Modes.Keys
            .Where(AmplitudeCapabilityMatrix.Writes.Contains);

        var legacy = Matrix(AmplitudeVersion.Legacy).Modes.Keys
            .Where(AmplitudeCapabilityMatrix.Writes.Contains);

        up.Should().BeEquivalentTo(legacy);
    }

    [Fact]
    public void No_write_of_a_pre_up_installation_is_ever_declared_real_time()
    {
        // The mode is a statement about the carrier, not about speed. RealTime would make
        // IntegrationModuleFacade wait for a synchronous answer, while a batch command is
        // deliberately left open until an acknowledgement file closes it (INT-24/INT-25) — the
        // acknowledgement whose format is question 4 to SBS.
        var matrix = Matrix(AmplitudeVersion.Legacy);

        foreach (var write in AmplitudeCapabilityMatrix.Writes)
            matrix.IsRealTime(write).Should().BeFalse($"{write} is never served live before Up");
    }

    [Fact]
    public void An_up_installation_on_a_batch_connection_declares_its_writes_as_batch()
    {
        var matrix = Matrix(AmplitudeVersion.Up, IntegrationMode.Batch);

        // The load-bearing case of the version↔mode reconciliation, and the one a version-only
        // matrix would get wrong. ExecuteIntegrationCommandHandler diverts a Batch connection's
        // commands to the file socle BEFORE resolving an adapter, so declaring RealTime here would
        // describe a path that does not exist and have the facade wait on it.
        foreach (var write in AmplitudeCapabilityMatrix.Writes)
            matrix.ModeOf(write).Should().Be(CapabilityMode.Batch);
    }

    [Fact]
    public void An_up_installation_keeps_its_live_reads_even_on_a_batch_connection()
    {
        var matrix = Matrix(AmplitudeVersion.Up, IntegrationMode.Batch);

        // The other half of the same reconciliation, and it goes the other way: a read is not a
        // command, nothing enlists one in a file, and the read path never consults the mode. Perfect
        // Vision is the precedent — a Batch-mode connection whose read-only view is read live. An
        // installation that chose files for its writes still answers a balance in the second.
        foreach (var read in AmplitudeCapabilityMatrix.ApiReads)
        {
            matrix.Supports(read).Should().BeTrue($"{read} is an API service of this release");
            matrix.ModeOf(read).Should().Be(CapabilityMode.RealTime);
        }
    }

    [Fact]
    public void A_pre_up_installation_declares_no_read_at_all()
    {
        var matrix = Matrix(AmplitudeVersion.Legacy);

        foreach (var read in AmplitudeCapabilityMatrix.ApiReads)
        {
            // Absent and not Batch. ModeOf answering null is what makes IntegrationModuleFacade
            // fall back to the INT-21 snapshot with no branch of its own — and declaring a read as
            // Batch would promise a carrier this socle does not have: there is no CommandType for a
            // read, so nothing could ever defer one to a file cycle.
            matrix.Supports(read).Should().BeFalse($"{read} needs an API this release has none of");
            matrix.ModeOf(read).Should().BeNull();
        }
    }

    [Fact]
    public void The_live_balance_read_is_the_entry_the_facade_branches_on()
    {
        // Singled out because IntegrationModuleFacade reads IsRealTime(ReadBalance) and nothing
        // else: true, and it asks the adapter; false or absent, and it serves the stale snapshot
        // figure. Both directions are wrong in a way a user sees — a button that never answers, or
        // a stale figure where a live one existed — so both are pinned.
        Matrix(AmplitudeVersion.Up, IntegrationMode.Api)
            .IsRealTime(IntegrationCapability.ReadBalance).Should().BeTrue();

        Matrix(AmplitudeVersion.Legacy)
            .IsRealTime(IntegrationCapability.ReadBalance).Should().BeFalse();
    }

    [Fact]
    public void The_difference_between_the_two_releases_is_the_three_reads_and_the_write_mode()
    {
        var up = Matrix(AmplitudeVersion.Up, IntegrationMode.Api).ToDictionary();
        var legacy = Matrix(AmplitudeVersion.Legacy).ToDictionary();

        // The whole difference between two installations of the same product, stated once. That is
        // the claim criterion 3 makes, and the reason the matrix is computed rather than constant.
        up.Keys.Except(legacy.Keys).Should().BeEquivalentTo(
            AmplitudeCapabilityMatrix.ApiReads.Select(r => r.ToString()));

        legacy.Keys.Except(up.Keys).Should().BeEmpty();

        up.Values.Should().AllBe(nameof(CapabilityMode.RealTime));
        legacy.Values.Should().AllBe(nameof(CapabilityMode.Batch));
    }

    [Fact]
    public void The_relay_mode_changes_nothing_about_an_up_matrix()
    {
        // The same installation reached through the on-premise agent (INT-26) is the same
        // installation. If the transport changed the matrix, a tenant that moved behind a relay
        // would lose buttons for a reason that has nothing to do with what its CBS can do.
        Matrix(AmplitudeVersion.Up, IntegrationMode.Relay).ToDictionary()
            .Should().BeEquivalentTo(Matrix(AmplitudeVersion.Up, IntegrationMode.Api).ToDictionary());
    }

    [Fact]
    public void An_incoherent_configuration_still_declares_the_batch_writes()
    {
        var matrix = Matrix(AmplitudeVersion.Legacy, IntegrationMode.Api);

        // A pre-Up release on an Api connection is a mode mistake, reported by the health check as
        // a configuration fault. The matrix does not pile on: the product does support those five
        // operations, and hiding every button would misdescribe Amplitude over a field that is one
        // edit away. What it must not do is declare them RealTime, which is what an unreconciled
        // "mode says Api" would have done.
        foreach (var write in AmplitudeCapabilityMatrix.Writes)
            matrix.ModeOf(write).Should().Be(CapabilityMode.Batch);

        matrix.Supports(IntegrationCapability.ReadBalance).Should().BeFalse();
    }

    [Fact]
    public void Unreadable_settings_narrow_the_matrix_and_never_widen_it()
    {
        var matrix = AmplitudeCapabilityMatrix.For(null, IntegrationMode.Api);

        // The writes are a property of the product and stay; the live reads are a property of the
        // release and drop out. Narrowing is the only safe direction on missing configuration: a
        // matrix that widened would offer live reads — and live writes — to an installation that may
        // have no API at all.
        foreach (var write in AmplitudeCapabilityMatrix.Writes)
            matrix.ModeOf(write).Should().Be(CapabilityMode.Batch);

        foreach (var read in AmplitudeCapabilityMatrix.ApiReads)
            matrix.Supports(read).Should().BeFalse();
    }

    [Fact]
    public void The_capabilities_neither_release_serves_are_declared_by_neither()
    {
        foreach (var matrix in new[]
                 {
                     Matrix(AmplitudeVersion.Up, IntegrationMode.Api),
                     Matrix(AmplitudeVersion.Legacy),
                 })
        {
            // ReadKycLevel: impossible on a batch CBS for the reason ICbsKycLevelPort states
            // itself, and unevidenced on Up — declaring it would put INT-21's compliance divergence
            // check behind a service no document mentions. ReadTransactions and ReadMonthlyFlow
            // belong to INT-13/INT-22 and to no criterion of INT-31. DebitAccount and ReverseDebit
            // belong to ASS-05. An absent capability is refused by
            // IntegrationAdapterResolver.ResolvePort before any call is made, which is stronger
            // than a method that says no.
            var notDeclared = new[]
            {
                IntegrationCapability.ReadKycLevel,
                IntegrationCapability.ReadTransactions,
                IntegrationCapability.ReadMonthlyFlow,
                IntegrationCapability.DebitAccount,
                IntegrationCapability.ReverseDebit,
            };

            foreach (var capability in notDeclared)
                matrix.Supports(capability).Should().BeFalse($"{capability} is not served here");
        }
    }

    [Fact]
    public void Every_declared_capability_is_backed_by_a_port_the_adapter_implements()
    {
        using var harness = AmplitudeHarness.With(AmplitudeVersion.Up, IntegrationMode.Api);

        var matrix = harness.Adapter.Capabilities;

        // A capability declared without the interface behind it is refused by
        // IntegrationAdapterResolver.ResolvePort with CapabilityNotSupported — the same code as
        // "cannot do this", which would send an administrator looking for a missing feature instead
        // of a missing registration. Asserted against the widest matrix of the two releases, since
        // that is the one that could over-declare.
        foreach (var capability in matrix.Modes.Keys)
        {
            var port = capability switch
            {
                IntegrationCapability.CreateCustomer
                    or IntegrationCapability.UpdateCustomer
                    or IntegrationCapability.SetKycLevel => typeof(ICbsCustomerPort),
                IntegrationCapability.OpenAccount
                    or IntegrationCapability.ReadAccounts
                    or IntegrationCapability.ReadBalance => typeof(ICbsAccountPort),
                IntegrationCapability.SubmitLoanApplication
                    or IntegrationCapability.ReadLoans => typeof(ICbsLoanPort),
                _ => null,
            };

            port.Should().NotBeNull($"{capability} is declared, so a port must answer it");
            port!.IsInstanceOfType(harness.Adapter).Should().BeTrue();
        }
    }
}
