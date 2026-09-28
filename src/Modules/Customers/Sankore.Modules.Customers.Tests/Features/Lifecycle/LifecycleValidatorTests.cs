namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle.ArchiveClient;
using Sankore.Modules.Customers.Features.Lifecycle.AssignAdvisor;
using Sankore.Modules.Customers.Features.Lifecycle.ReactivateClient;
using Sankore.Modules.Customers.Features.Lifecycle.SuspendClient;
using Sankore.Modules.Customers.Features.Lifecycle.TransferClient;
using Xunit;

public sealed class LifecycleValidatorTests
{
    private static readonly Guid ClientId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid AdvisorId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    [Fact]
    public void Suspend_rejects_a_missing_motive_with_the_REASON_REQUIRED_code()
    {
        var result = new SuspendClientValidator().Validate(new SuspendClientCommand(ClientId, ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == CustomerErrors.ReasonRequired);
    }

    [Fact]
    public void Suspend_rejects_a_motive_longer_than_the_column()
    {
        var result = new SuspendClientValidator().Validate(
            new SuspendClientCommand(ClientId, new string('x', 1001)));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Suspend_accepts_a_well_formed_command()
    {
        new SuspendClientValidator()
            .Validate(new SuspendClientCommand(ClientId, "Fraud suspicion"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Archive_rejects_a_missing_motive_with_the_REASON_REQUIRED_code()
    {
        var result = new ArchiveClientValidator().Validate(new ArchiveClientCommand(ClientId, ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == CustomerErrors.ReasonRequired);
    }

    [Fact]
    public void Transfer_rejects_an_empty_target_agency()
    {
        var result = new TransferClientValidator().Validate(
            new TransferClientCommand(ClientId, Guid.Empty, "Client moved"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Transfer_rejects_a_missing_motive_with_the_REASON_REQUIRED_code()
    {
        var result = new TransferClientValidator().Validate(
            new TransferClientCommand(ClientId, AgencyId, ""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == CustomerErrors.ReasonRequired);
    }

    [Fact]
    public void Transfer_accepts_a_well_formed_command()
    {
        new TransferClientValidator()
            .Validate(new TransferClientCommand(ClientId, AgencyId, "Client moved to Bouaké"))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void AssignAdvisor_accepts_a_null_advisor_because_null_clears_the_assignment()
    {
        new AssignAdvisorValidator()
            .Validate(new AssignAdvisorCommand(ClientId, null))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void AssignAdvisor_rejects_an_all_zero_advisor_id()
    {
        // An explicit Guid.Empty is a caller bug, not the intention to clear.
        new AssignAdvisorValidator()
            .Validate(new AssignAdvisorCommand(ClientId, Guid.Empty))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void AssignAdvisor_accepts_a_real_advisor_id()
    {
        new AssignAdvisorValidator()
            .Validate(new AssignAdvisorCommand(ClientId, AdvisorId))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Reactivate_rejects_an_empty_client_id()
    {
        new ReactivateClientValidator()
            .Validate(new ReactivateClientCommand(Guid.Empty))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Every_mutation_declares_the_resource_it_audits()
    {
        // ICommand + IResourceCommand is what makes AuditBehavior record these
        // transitions against the client; a missing ResourceId silently loses the
        // ability to query "everything that happened to client X".
        new SuspendClientCommand(ClientId, "r").ResourceType.Should().Be("Client");
        new SuspendClientCommand(ClientId, "r").ResourceId.Should().Be(ClientId.ToString());
        new ReactivateClientCommand(ClientId).ResourceId.Should().Be(ClientId.ToString());
        new ArchiveClientCommand(ClientId, "r").ResourceId.Should().Be(ClientId.ToString());
        new AssignAdvisorCommand(ClientId, null).ResourceId.Should().Be(ClientId.ToString());
        new TransferClientCommand(ClientId, AgencyId, "r").ResourceId.Should().Be(ClientId.ToString());
    }
}
