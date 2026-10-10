namespace Sankore.Modules.Integration.Tests.Adapters.Sab;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Sab;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// The matrix — the part of INT-32 that is deliverable without SBS, and the one a screen depends
/// on: a front end reads it to decide which buttons exist at all, so a capability declared in the
/// wrong mode is a button that waits for an answer nothing will send, and one declared that the
/// installation cannot serve is a button that fails in front of a client.
/// </summary>
public sealed class SabCapabilityMatrixTests
{
    [Fact]
    public void Every_declared_operation_is_declared_live()
    {
        var matrix = SabCapabilityMatrix.For(SabHarness.ScopedSettings());

        foreach (var capability in SabCapabilityMatrix.LiveOperations)
        {
            matrix.Supports(capability).Should().BeTrue($"{capability} is in INT-32's scope");

            // RealTime is a statement about the carrier, and the pairing is load-bearing:
            // IntegrationModuleFacade reads IsRealTime(ReadBalance) and goes straight to the stale
            // snapshot when it is false. Declaring Batch for an HTTP API would hand a counter
            // last night's figure while the installation answered in the second.
            matrix.ModeOf(capability).Should().Be(CapabilityMode.RealTime);
        }
    }

    [Fact]
    public void Nothing_is_ever_declared_in_batch_mode()
    {
        // Open SAB has no file cycle, and SabSettings is not a BatchCapableSettings — the settings
        // validator refuses IntegrationMode.Batch for this kind outright. A Batch entry here would
        // declare a command could be closed by an acknowledgement file that no part of this
        // integration produces.
        var matrix = SabCapabilityMatrix.For(SabHarness.ScopedSettings());

        matrix.Modes.Values.Should().OnlyContain(mode => mode == CapabilityMode.RealTime);
    }

    [Fact]
    public void An_installation_with_no_entity_declares_nothing_at_all()
    {
        var matrix = SabCapabilityMatrix.For(SabHarness.UnscopedSettings());

        // Criterion 2 showing up in the matrix and not only on the call path. Every port method
        // refuses on the entity guard first, so declaring capabilities would offer ten buttons
        // whose every press answers a configuration error — on the one connection where the
        // reason for the refusal is that the call might otherwise reach another institution.
        matrix.Modes.Should().BeEmpty();

        foreach (var capability in SabCapabilityMatrix.LiveOperations)
            matrix.ModeOf(capability).Should().BeNull();
    }

    [Fact]
    public void Unreadable_settings_narrow_the_matrix_and_never_widen_it()
    {
        // Null is a tenant with no SAB connection, or a SAB row carrying another kind's settings.
        // Narrowing is the only safe direction on missing configuration: a matrix that widened
        // would offer a live customer write against an installation this deployment cannot even
        // address.
        SabCapabilityMatrix.For(null).Modes.Should().BeEmpty();
    }

    [Fact]
    public void The_entity_is_the_only_thing_that_changes_the_matrix()
    {
        var scoped = SabCapabilityMatrix.For(SabHarness.ScopedSettings()).ToDictionary();

        var richer = SabCapabilityMatrix.For(new Domain.SabSettings
        {
            BaseUrl = "https://other.example.invalid",
            Entity = SabHarness.PlaceholderEntity,
            CredentialVaultRef = "vault://placeholder",
            RateLimitPerMinute = 600,
            TimeoutSeconds = 45,
        }).ToDictionary();

        // The base URL, the rate limit and the vault reference are coordinates, not capabilities.
        // If they moved the matrix, two installations of the same product would offer different
        // buttons for reasons that have nothing to do with what SAB AT can do.
        richer.Should().BeEquivalentTo(scoped);
    }

    [Fact]
    public void The_operations_outside_INT_32_are_not_declared()
    {
        var matrix = SabCapabilityMatrix.For(SabHarness.ScopedSettings());

        // ReadKycLevel: question 3 — whether Open SAB exposes the tier as a readable field is
        // unknown, and ICbsKycLevelPort's contract requires an implementer to declare it, so
        // claiming it would have INT-21 compute a compliance divergence from a figure that cannot
        // be obtained. DebitAccount / ReverseDebit: ASS-05, out of this chantier's criteria, the
        // same position TemenosAdapter takes. The insurance capabilities are another family's.
        var notDeclared = new[]
        {
            IntegrationCapability.ReadKycLevel,
            IntegrationCapability.DebitAccount,
            IntegrationCapability.ReverseDebit,
            IntegrationCapability.SubscribePolicy,
            IntegrationCapability.DeclareClaim,
            IntegrationCapability.PriceProduct,
        };

        foreach (var capability in notDeclared)
            matrix.Supports(capability).Should().BeFalse($"{capability} is not served by this adapter");
    }

    [Fact]
    public void Every_declared_capability_is_backed_by_a_port_the_adapter_implements()
    {
        var matrix = SabCapabilityMatrix.For(SabHarness.ScopedSettings());

        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        // A capability declared without the interface behind it is refused by
        // IntegrationAdapterResolver.ResolvePort with CapabilityNotSupported — the same code as
        // "cannot do this", which would send an administrator looking for a missing feature
        // instead of a missing port.
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
                IntegrationCapability.ReadTransactions
                    or IntegrationCapability.ReadMonthlyFlow => typeof(ICbsTransactionPort),
                IntegrationCapability.SubmitLoanApplication
                    or IntegrationCapability.ReadLoans => typeof(ICbsLoanPort),
                _ => null,
            };

            port.Should().NotBeNull($"{capability} is declared, so a port must answer it");
            port!.IsInstanceOfType(harness.Adapter).Should().BeTrue();
        }
    }

    [Fact]
    public void The_matrix_is_read_from_the_connection_it_is_handed()
    {
        // Was two harnesses compared through a parameterless Capabilities property, which could
        // only ever express "this KIND's matrix". Same purpose — the entity scope of criterion 2
        // decides the matrix — asserted the way the contract now states it: ONE adapter, two rows,
        // two answers.
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var scoped = SabHarness.ConnectionCarrying(SabHarness.ScopedSettings());
        var unscoped = SabHarness.ConnectionCarrying(SabHarness.UnscopedSettings());

        harness.Adapter.CapabilitiesFor(scoped).Supports(IntegrationCapability.CreateCustomer)
            .Should().BeTrue("this installation names the institution a call is about");

        harness.Adapter.CapabilitiesFor(unscoped).Modes
            .Should().BeEmpty("this one does not, and the matrix must say so");
    }

    [Fact]
    public void A_connection_that_is_not_active_still_yields_a_matrix()
    {
        // Deliberate, and the opposite of TemenosAdapter's binding. A SAB connection can never be
        // activated — its health check cannot pass until the catalogue arrives — so refusing an
        // inactive row would make the matrix permanently empty and the deliverable half of this
        // chantier unobservable. Answering for one grants nothing: every port refuses regardless,
        // and the facade's own GetCapabilities resolves an ACTIVE connection, so no screen is
        // offered these buttons today.
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        // ConnectionCarrying never activates the row it builds, which is the state under test.
        var inactive = SabHarness.ConnectionCarrying(SabHarness.ScopedSettings());

        inactive.IsActive.Should().BeFalse("otherwise this test is about an active connection");

        harness.Adapter.CapabilitiesFor(inactive).Modes.Should().NotBeEmpty();
    }

    [Fact]
    public void The_relay_mode_changes_nothing_about_the_matrix()
    {
        // The same installation reached through the on-premise agent (INT-26) is the same
        // installation. If the carrier changed the matrix, a tenant that moved behind a relay
        // would lose buttons for a reason that has nothing to do with what its CBS can do.
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var direct = SabHarness.ConnectionCarrying(SabHarness.ScopedSettings());

        var relayed = SabHarness.ConnectionCarrying(
            SabHarness.ScopedSettings(), IntegrationMode.Relay);

        harness.Adapter.CapabilitiesFor(relayed).ToDictionary()
            .Should().BeEquivalentTo(harness.Adapter.CapabilitiesFor(direct).ToDictionary());
    }

    [Fact]
    public void A_row_carrying_another_kinds_settings_gets_an_empty_matrix_rather_than_an_exception()
    {
        // Was "a tenant with no connection": the matrix used to be looked up by the adapter, so
        // "no row" was the narrowest input it could be given. CapabilitiesFor takes a row, so the
        // narrowest input is now a row whose settings it cannot read — the `as` in CapabilitiesFor,
        // and the one that reaches a screen only asking what is available.
        using var harness = SabHarness.With(SabHarness.ScopedSettings());

        var foreign = SabHarness.ConnectionCarrying(
            new TemenosSettings { BaseUrl = "https://example.invalid" });

        harness.Adapter.CapabilitiesFor(foreign).Modes.Should().BeEmpty();
    }
}
