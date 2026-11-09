namespace Sankore.Api.Tests.Features.StartingData;

using FluentAssertions;
using Sankore.Api.Features.StartingData;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The API checks a load again against the registry read fresh (docs/donnees-demarrage-demo.md): a
/// test flag lifted, or a twin, between the request and the run must not receive fictitious data.
/// </summary>
public sealed class DataLoadGateTests
{
    private static TenantInfo Tenant(bool isTest = false, Guid? parent = null)
        => new(Guid.NewGuid(), "Caisse Avenir", "https://crm.caisse.ci", true, false, null, null, IsTest: isTest, ParentTenantId: parent);

    [Fact]
    public void The_starter_goes_to_any_institution_that_is_not_a_twin()
    {
        DataLoadGate.Refusal(Tenant(), StartingDataKind.Starter).Should().BeNull();
        DataLoadGate.Refusal(Tenant(isTest: true, parent: Guid.NewGuid()), StartingDataKind.Starter)
            .Should().StartWith("DATA_LOAD_ON_TEST_TWIN");
    }

    [Fact]
    public void The_demo_only_goes_to_a_test_institution()
    {
        DataLoadGate.Refusal(Tenant(), StartingDataKind.Demo).Should().StartWith("DEMO_REQUIRES_TEST_TENANT");
        DataLoadGate.Refusal(Tenant(isTest: true), StartingDataKind.Demo).Should().BeNull();
    }

    [Fact]
    public void An_unreadable_institution_or_kind_loads_nothing()
    {
        DataLoadGate.Refusal(null, StartingDataKind.Starter).Should().StartWith("TENANT_NOT_FOUND");
        DataLoadGate.Refusal(Tenant(), null).Should().StartWith("DATA_LOAD_KIND_UNKNOWN");
    }

    [Fact]
    public void The_system_account_password_is_strong_and_never_the_same()
    {
        var first = TenantDataLoadJob.RandomPassword();
        first.Should().HaveLength(32);
        first.Should().MatchRegex("[0-9]").And.MatchRegex("[a-z]").And.MatchRegex("[A-Z]").And.MatchRegex("[^A-Za-z0-9]");
        TenantDataLoadJob.RandomPassword().Should().NotBe(first);
    }
}
