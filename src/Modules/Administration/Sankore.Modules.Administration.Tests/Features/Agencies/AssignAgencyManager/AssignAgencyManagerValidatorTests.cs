namespace Sankore.Modules.Administration.Tests.Features.Agencies.AssignAgencyManager;

using FluentAssertions;
using Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;
using Xunit;

public sealed class AssignAgencyManagerValidatorTests
{
    private readonly AssignAgencyManagerValidator _validator = new();

    [Fact]
    public void Accepts_two_real_identifiers()
        => _validator.Validate(new AssignAgencyManagerCommand(Guid.NewGuid(), Guid.NewGuid()))
            .IsValid.Should().BeTrue();

    [Fact]
    public void Rejects_an_empty_agency_id()
    {
        var result = _validator.Validate(new AssignAgencyManagerCommand(Guid.Empty, Guid.NewGuid()));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.PropertyName == nameof(AssignAgencyManagerCommand.AgencyId));
    }

    [Fact]
    public void Rejects_an_empty_manager_id()
    {
        var result = _validator.Validate(new AssignAgencyManagerCommand(Guid.NewGuid(), Guid.Empty));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e =>
            e.PropertyName == nameof(AssignAgencyManagerCommand.ManagerUserId));
    }
}
