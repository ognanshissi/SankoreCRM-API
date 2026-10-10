namespace Sankore.Modules.Integration.Tests.Adapters.PerfectVision;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.PerfectVision;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-28, criterion 3 — « la matrice de capacités déclare les modes réellement supportés ».
///
/// <para>
/// The deliverable half of INT-28, and the one a screen depends on: a front end reads this matrix
/// to decide which buttons exist at all, so a capability declared in the wrong mode is a button
/// that waits for an answer nothing will send, and one declared that the installation cannot
/// serve is a button that fails in front of a client.
/// </para>
/// </summary>
public sealed class PerfectVisionCapabilityMatrixTests
{
    [Fact]
    public void The_writes_are_declared_in_batch_mode()
    {
        var matrix = PerfectVisionCapabilityMatrix.For(new PerfectVisionSettings());

        foreach (var write in PerfectVisionCapabilityMatrix.BatchWrites)
        {
            matrix.Supports(write).Should().BeTrue($"{write} travels in the outbound file");
            matrix.ModeOf(write).Should().Be(CapabilityMode.Batch);
        }
    }

    [Fact]
    public void No_write_is_ever_declared_real_time()
    {
        // The mode is a statement about the carrier, not about speed. RealTime would make
        // IntegrationModuleFacade wait for a synchronous answer, while a batch command is
        // deliberately left open until an acknowledgement file closes it (INT-24/INT-25) — the
        // acknowledgement whose format is question 2 to the vendor.
        var writes = new[]
        {
            IntegrationCapability.CreateCustomer,
            IntegrationCapability.UpdateCustomer,
            IntegrationCapability.SetKycLevel,
            IntegrationCapability.OpenAccount,
            IntegrationCapability.SubmitLoanApplication,
            IntegrationCapability.DebitAccount,
            IntegrationCapability.ReverseDebit,
        };

        foreach (var settings in new[]
                 {
                     new PerfectVisionSettings(),
                     new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" },
                 })
        {
            var matrix = PerfectVisionCapabilityMatrix.For(settings);

            foreach (var write in writes)
                matrix.IsRealTime(write).Should().BeFalse($"{write} is never served live");
        }
    }

    [Fact]
    public void A_configured_view_adds_the_balance_read_as_a_live_one()
    {
        var matrix = PerfectVisionCapabilityMatrix.For(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" });

        matrix.Supports(IntegrationCapability.ReadBalance).Should().BeTrue();

        // RealTime specifically, and the pairing is load-bearing: IntegrationModuleFacade reads
        // IsRealTime(ReadBalance) and goes straight to the stale snapshot figure when it is false.
        // Declaring Batch here would silently disable the live read this view exists to serve.
        matrix.ModeOf(IntegrationCapability.ReadBalance).Should().Be(CapabilityMode.RealTime);
    }

    [Fact]
    public void Without_a_view_the_balance_read_is_absent_from_the_matrix()
    {
        var matrix = PerfectVisionCapabilityMatrix.For(new PerfectVisionSettings());

        // Absent and not Batch. ModeOf answering null is what makes the facade fall back to the
        // snapshot with no branch of its own — INT-28's « sinon le snapshot », obtained from the
        // matrix alone.
        matrix.Supports(IntegrationCapability.ReadBalance).Should().BeFalse();
        matrix.ModeOf(IntegrationCapability.ReadBalance).Should().BeNull();
    }

    [Fact]
    public void Unreadable_settings_narrow_the_matrix_and_never_widen_it()
    {
        var matrix = PerfectVisionCapabilityMatrix.For(null);

        // The batch writes are a property of the product and stay; the view is a property of the
        // installation and drops out. Narrowing is the only safe direction on missing
        // configuration: a matrix that widened would offer a live balance to an installation that
        // has no view at all.
        matrix.Supports(IntegrationCapability.ReadBalance).Should().BeFalse();
        matrix.Supports(IntegrationCapability.CreateCustomer).Should().BeTrue();
    }

    [Fact]
    public void The_queries_a_batch_core_banking_system_cannot_answer_are_not_declared()
    {
        var matrix = PerfectVisionCapabilityMatrix.For(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" });

        // Transaction history, monthly flow, accounts, loans and the KYC tier are served by the
        // INT-21 snapshot. ReadKycLevel in particular is absent for the reason ICbsKycLevelPort
        // states itself: a batch CBS accepts the write and offers no way to read the tier back —
        // and the adapter does not even claim that interface, which says "never" rather than
        // "not yet".
        var notDeclared = new[]
        {
            IntegrationCapability.ReadAccounts,
            IntegrationCapability.ReadTransactions,
            IntegrationCapability.ReadMonthlyFlow,
            IntegrationCapability.ReadLoans,
            IntegrationCapability.ReadKycLevel,
            IntegrationCapability.DebitAccount,
            IntegrationCapability.ReverseDebit,
        };

        foreach (var capability in notDeclared)
            matrix.Supports(capability).Should().BeFalse($"{capability} is not served by this adapter");
    }

    [Fact]
    public void The_matrix_changes_with_the_settings_and_with_nothing_else()
    {
        var withoutView = PerfectVisionCapabilityMatrix.For(new PerfectVisionSettings())
            .ToDictionary();

        var withView = PerfectVisionCapabilityMatrix
            .For(new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" })
            .ToDictionary();

        // The whole difference between two installations of the same product is one entry. That is
        // the claim criterion 3 makes, and the reason the matrix is computed rather than constant.
        withView.Keys.Except(withoutView.Keys)
            .Should().ContainSingle().Which.Should().Be(nameof(IntegrationCapability.ReadBalance));

        withoutView.Keys.Except(withView.Keys).Should().BeEmpty();
    }

    [Fact]
    public void Every_declared_capability_is_backed_by_a_port_the_adapter_implements()
    {
        var matrix = PerfectVisionCapabilityMatrix.For(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" });

        using var harness = PerfectVisionHarness.With(
            new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" });

        // A capability declared without the interface behind it is refused by
        // IntegrationAdapterResolver.ResolvePort with CapabilityNotSupported — the same code as
        // "cannot do this", which would send an administrator looking for a missing feature
        // instead of a missing registration.
        foreach (var capability in matrix.Modes.Keys)
        {
            var port = capability switch
            {
                IntegrationCapability.CreateCustomer
                    or IntegrationCapability.UpdateCustomer
                    or IntegrationCapability.SetKycLevel => typeof(ICbsCustomerPort),
                IntegrationCapability.OpenAccount
                    or IntegrationCapability.ReadBalance => typeof(ICbsAccountPort),
                IntegrationCapability.SubmitLoanApplication => typeof(ICbsLoanPort),
                _ => null,
            };

            port.Should().NotBeNull($"{capability} is declared, so a port must answer it");
            port!.IsInstanceOfType(harness.Adapter).Should().BeTrue();
        }
    }
}
