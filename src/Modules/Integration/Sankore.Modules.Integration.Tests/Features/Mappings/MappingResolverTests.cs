namespace Sankore.Modules.Integration.Tests.Features.Mappings;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Mappings;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// The resolver is what criterion 4 of INT-04 lives in, and what every adapter calls. Both
/// directions are covered, and so is the DETAIL of the failure: a message that stops naming the
/// domain and the code turns an administrator's five-minute fix back into a log dig.
/// </summary>
public sealed class MappingResolverTests
{
    private static readonly Guid ConnectionId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ResolveAsync_returns_the_external_code()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.IdDocType, "CNI", "ID_CARD"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new MappingResolver(db).ResolveAsync(
            factory.TenantId, ConnectionId, MappingDomain.IdDocType, "CNI", default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ID_CARD");
    }

    [Fact]
    public async Task ResolveAsync_trims_the_requested_code()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Country, "CI", "CIV"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var result = await new MappingResolver(db).ResolveAsync(
            factory.TenantId, ConnectionId, MappingDomain.Country, "  CI  ", default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveAsync_is_a_Technical_failure_naming_the_domain_and_the_code()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());
        await using var db = factory.CreateContext();

        var result = await new MappingResolver(db).ResolveAsync(
            factory.TenantId, ConnectionId, MappingDomain.Profession, "COMMERCANTE", default);

        result.IsFailure.Should().BeTrue();
        result.Code.Should().Be(IntegrationErrors.MappingMissing);

        // Technical and NOT Transient: retrying cannot invent a mapping, and the dispatcher must
        // park the command and alert an administrator instead of burning its attempt budget.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.IsRetryable.Should().BeFalse();

        // Criterion 4, literally: the detail names the domain AND the code.
        result.Detail.Should().Contain("Profession").And.Contain("COMMERCANTE");
    }

    [Fact]
    public async Task ResolveAsync_never_falls_back_to_the_crm_code()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());
        await using var db = factory.CreateContext();

        var result = await new MappingResolver(db).ResolveAsync(
            factory.TenantId, ConnectionId, MappingDomain.Gender, "F", default);

        // The whole point of the type: a missing mapping has no Value to read. A pass-through
        // would create the customer at the CBS with a code nobody there can interpret, and the
        // call would look successful.
        result.IsFailure.Should().BeTrue();
        result.Invoking(r => r.Value).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ResolveAsync_does_not_read_another_tenants_mapping()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());
        var otherTenant = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                otherTenant, ConnectionId, MappingDomain.Product, "EPARGNE", "SAV001"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();
        var resolver = new MappingResolver(db);

        // Same connection id, same code, the other tenant's row: IgnoreQueryFilters is only safe
        // because the explicit TenantId predicate replaces what it switched off.
        var mine = await resolver.ResolveAsync(
            factory.TenantId, ConnectionId, MappingDomain.Product, "EPARGNE", default);

        var theirs = await resolver.ResolveAsync(
            otherTenant, ConnectionId, MappingDomain.Product, "EPARGNE", default);

        mine.IsFailure.Should().BeTrue();
        theirs.IsSuccess.Should().BeTrue();
        theirs.Value.Should().Be("SAV001");
    }

    [Fact]
    public async Task ResolveAsync_works_for_the_tenant_the_caller_names_not_the_ambient_one()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());
        var backgroundTenant = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                backgroundTenant, ConnectionId, MappingDomain.Agency, "AG-ABJ-01", "BR001"));
            await seed.SaveChangesAsync();
        }

        // A context bound to Guid.Empty, the way a Hangfire job or a consumer sees the world
        // before any JWT: the resolver must still answer for the tenant it was given.
        await using var db = factory.ContextFor(Guid.Empty);

        var result = await new MappingResolver(db).ResolveAsync(
            backgroundTenant, ConnectionId, MappingDomain.Agency, "AG-ABJ-01", default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("BR001");
    }

    [Fact]
    public async Task ReverseAsync_translates_an_external_code_back()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.MaritalStatus, "Married", "M"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var crmCode = await new MappingResolver(db).ReverseAsync(
            factory.TenantId, ConnectionId, MappingDomain.MaritalStatus, "M", default);

        crmCode.Should().Be("Married");
    }

    [Fact]
    public async Task ReverseAsync_returns_null_rather_than_failing()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());
        await using var db = factory.CreateContext();

        var crmCode = await new MappingResolver(db).ReverseAsync(
            factory.TenantId, ConnectionId, MappingDomain.Sector, "UNKNOWN_SECTOR", default);

        // Null and not an exception: this serves the snapshot synchronisation, and one unknown
        // code from the CBS must not abandon the whole stream.
        crmCode.Should().BeNull();
    }

    [Fact]
    public async Task ReverseAsync_is_deterministic_when_two_crm_codes_fold_onto_one()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());

        await using (var seed = factory.CreateContext())
        {
            // Allowed on purpose: the reverse index is not unique, because two CRM products may
            // legitimately be the same product at the CBS.
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Product, "TONTINE_B", "SAV001"));
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                factory.TenantId, ConnectionId, MappingDomain.Product, "TONTINE_A", "SAV001"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var first = await new MappingResolver(db).ReverseAsync(
            factory.TenantId, ConnectionId, MappingDomain.Product, "SAV001", default);

        // Ordered by CRM code, so the answer does not depend on physical row order.
        first.Should().Be("TONTINE_A");
    }

    [Fact]
    public async Task ReverseAsync_does_not_read_another_tenants_mapping()
    {
        using var factory = new TestIntegrationDbContextFactory(Guid.NewGuid());
        var otherTenant = Guid.NewGuid();

        await using (var seed = factory.CreateContext())
        {
            seed.Mappings.Add(MappingsTestFixtures.Mapping(
                otherTenant, ConnectionId, MappingDomain.Country, "CI", "CIV"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateContext();

        var crmCode = await new MappingResolver(db).ReverseAsync(
            factory.TenantId, ConnectionId, MappingDomain.Country, "CIV", default);

        crmCode.Should().BeNull();
    }
}
