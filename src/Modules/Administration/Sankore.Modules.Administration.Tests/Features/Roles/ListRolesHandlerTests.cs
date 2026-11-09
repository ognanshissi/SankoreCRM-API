using NSubstitute;
using Sankore.Shared.Infrastructure.Auth;

namespace Sankore.Modules.Administration.Tests.Features.Roles;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Roles.ListRoles;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

public sealed class ListRolesHandlerTests: IDisposable
{
    private readonly TestAdminDbContextFactory _factory = new(Guid.NewGuid());

    public void Dispose() => _factory.Dispose();

    // ── S1: Returns all assignable roles ordered by name ─────────────────

    [Fact]
    public async Task Returns_assignable_roles_ordered_by_name()
    {
        await using var db = _factory.CreateContext();
        var cu = Substitute.For<ICurrentUser>();
        db.Roles.AddRange(
            AppRole.Create("BranchManager", "Branch Manager"),
            AppRole.Create("Agent", "Agent"),
            AppRole.Create("Administrator", "Administrateur"));
        await db.SaveChangesAsync();

        var handler = new ListRolesHandler(db, cu);
        var result = await handler.Handle(new ListRolesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
        result.Value.Select(r => r.Name).Should().BeInAscendingOrder();
    }

    // ── S2: System role (IsAssignable=false) is excluded ─────────────────

    [Fact]
    public async Task Excludes_non_assignable_System_role()
    {
        await using var db = _factory.CreateContext();
        var cu = Substitute.For<ICurrentUser>();
        db.Roles.AddRange(
            AppRole.Create("System", "Compte technique", isSystem: true), // IsAssignable = false
            AppRole.Create("Agent", "Agent"));
        await db.SaveChangesAsync();

        var handler = new ListRolesHandler(db, cu);
        var result = await handler.Handle(new ListRolesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value.Single().Name.Should().Be("Agent");
    }

    // ── S3: Empty DB returns empty list ───────────────────────────────────

    [Fact]
    public async Task Returns_empty_list_when_no_roles()
    {
        await using var db = _factory.CreateContext();
        var cu = Substitute.For<ICurrentUser>();
        var handler = new ListRolesHandler(db, cu);
        var result = await handler.Handle(new ListRolesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    // ── Socle isolation, lot 2: a paid module's built-in roles only where it is in reach ──

    [Fact]
    public async Task A_socle_institution_does_not_see_the_banking_roles()
    {
        await using var db = _factory.CreateContext();
        db.Roles.AddRange(
            AppRole.Create("Agent", "Agent"),
            AppRole.Create("CreditAnalyst", "Analyste crédit"),
            AppRole.Create("Cashier", "Caissier"));
        await db.SaveChangesAsync();

        var socle = await new ListRolesHandler(db, Substitute.For<ICurrentUser>(), Sankore.Shared.Kernel.FixedTenantModules.None)
            .Handle(new ListRolesQuery(), CancellationToken.None);
        socle.Value!.Select(r => r.Name).Should().Equal("Agent");

        var banking = await new ListRolesHandler(db, Substitute.For<ICurrentUser>(),
                new Sankore.Shared.Kernel.FixedTenantModules(Sankore.Shared.Kernel.TenantModules.BankingMicrofinance))
            .Handle(new ListRolesQuery(), CancellationToken.None);
        banking.Value!.Select(r => r.Name).Should().Equal("Agent", "Cashier", "CreditAnalyst");
    }

    [Fact]
    public async Task A_switched_off_module_keeps_its_roles_visible()
    {
        await using var db = _factory.CreateContext();
        db.Roles.Add(AppRole.Create("CreditAnalyst", "Analyste crédit"));
        await db.SaveChangesAsync();

        var off = new Sankore.Shared.Kernel.FixedTenantModules { ReadOnly = [Sankore.Shared.Kernel.TenantModules.BankingMicrofinance] };
        (await new ListRolesHandler(db, Substitute.For<ICurrentUser>(), off).Handle(new ListRolesQuery(), CancellationToken.None))
            .Value!.Should().ContainSingle();
    }
}
