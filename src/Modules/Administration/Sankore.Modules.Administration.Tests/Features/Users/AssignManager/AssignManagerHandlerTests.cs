namespace Sankore.Modules.Administration.Tests.Features.Users.AssignManager;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.AssignManager;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

public sealed class AssignManagerHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly Guid _agencyId = Guid.NewGuid();

    public AssignManagerHandlerTests() => _factory = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private AppUser NewActiveUser(string email)
    {
        var user = AppUser.Create(_tenantId, _agencyId, "Awa", "Ouattara", email);
        user.Activate();
        return user;
    }

    private async Task<Guid[]> SeedUsersAsync(params string[] emails)
    {
        await using var db = _factory.CreateContext();
        var users = emails.Select(NewActiveUser).ToArray();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        return users.Select(u => u.Id).ToArray();
    }

    /// <summary>Wires a chain top-down: Link(a, b) makes a report to b.</summary>
    private async Task LinkAsync(Guid subordinate, Guid manager)
    {
        await using var db = _factory.CreateContext();
        var user = await db.Users.AsTracking().SingleAsync(u => u.Id == subordinate);
        user.ReportTo(manager);
        await db.SaveChangesAsync();
    }

    private AssignManagerHandler Assigner()
    {
        var db = _factory.CreateContext();
        return new AssignManagerHandler(db, new ReportingLine(db));
    }

    private ClearManagerHandler Clearer() => new(_factory.CreateContext());

    private async Task<Guid?> ManagerOfAsync(Guid userId)
    {
        await using var db = _factory.CreateContext();
        return db.Users.Single(u => u.Id == userId).ReportsToUserId;
    }

    // ── assigning ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Points_a_user_at_their_manager()
    {
        var ids = await SeedUsersAsync("a@sankore.ci", "b@sankore.ci");

        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[0], ids[1]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ManagerUserId.Should().Be(ids[1]);
        result.Value.ManagerFullName.Should().Be("Awa Ouattara");
        result.Value.PreviousManagerUserId.Should().BeNull();
        result.Value.Changed.Should().BeTrue();

        (await ManagerOfAsync(ids[0])).Should().Be(ids[1]);
    }

    [Fact]
    public async Task Reports_the_previous_manager_when_the_line_moves()
    {
        var ids = await SeedUsersAsync("a@sankore.ci", "b@sankore.ci", "c@sankore.ci");
        await Assigner().Handle(new AssignManagerCommand(ids[0], ids[1]), CancellationToken.None);

        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[0], ids[2]), CancellationToken.None);

        result.Value.PreviousManagerUserId.Should().Be(ids[1]);
        result.Value.ManagerUserId.Should().Be(ids[2]);
    }

    [Fact]
    public async Task Re_sending_the_same_manager_changes_nothing()
    {
        var ids = await SeedUsersAsync("a@sankore.ci", "b@sankore.ci");
        await Assigner().Handle(new AssignManagerCommand(ids[0], ids[1]), CancellationToken.None);

        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[0], ids[1]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Changed.Should().BeFalse();
    }

    [Fact]
    public async Task A_manager_from_another_agency_is_accepted()
    {
        // A head-office director manages branch staff: the reporting line is deliberately
        // independent of the agency a user belongs to.
        Guid subordinateId;
        Guid directorId;
        await using (var db = _factory.CreateContext())
        {
            var subordinate = NewActiveUser("branch@sankore.ci");
            var director = AppUser.Create(_tenantId, Guid.NewGuid(), "Koffi", "Kone", "hq@sankore.ci");
            director.Activate();
            db.Users.AddRange(subordinate, director);
            await db.SaveChangesAsync();
            subordinateId = subordinate.Id;
            directorId = director.Id;
        }

        var result = await Assigner().Handle(
            new AssignManagerCommand(subordinateId, directorId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ── cycles ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_a_user_reporting_to_themselves()
    {
        var ids = await SeedUsersAsync("a@sankore.ci");

        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[0], ids[0]), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AssignManagerErrors.SelfReportForbidden);
    }

    [Fact]
    public async Task Refuses_a_direct_two_person_loop()
    {
        var ids = await SeedUsersAsync("a@sankore.ci", "b@sankore.ci");
        await LinkAsync(ids[1], ids[0]);    // b reports to a

        // Making a report to b would close the loop.
        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[0], ids[1]), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AssignManagerErrors.ReportingCycle);
        (await ManagerOfAsync(ids[0])).Should().BeNull();
    }

    [Fact]
    public async Task Refuses_a_loop_through_intermediaries()
    {
        var ids = await SeedUsersAsync("a@sankore.ci", "b@sankore.ci", "c@sankore.ci", "d@sankore.ci");
        await LinkAsync(ids[1], ids[0]);    // b → a
        await LinkAsync(ids[2], ids[1]);    // c → b
        await LinkAsync(ids[3], ids[2]);    // d → c

        // a reporting to d would close a → d → c → b → a.
        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[0], ids[3]), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AssignManagerErrors.ReportingCycle);
    }

    [Fact]
    public async Task A_shared_manager_is_not_a_loop()
    {
        // Two people reporting to the same manager is the most ordinary shape there is; the
        // cycle check must not mistake a diamond for a loop.
        var ids = await SeedUsersAsync("a@sankore.ci", "b@sankore.ci", "boss@sankore.ci");
        await LinkAsync(ids[0], ids[2]);

        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[1], ids[2]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Moving_a_manager_under_their_own_subordinate_is_refused()
    {
        // The realistic version of the bug: a reorganisation that inverts two levels.
        var ids = await SeedUsersAsync("junior@sankore.ci", "senior@sankore.ci", "boss@sankore.ci");
        await LinkAsync(ids[0], ids[1]);    // junior → senior
        await LinkAsync(ids[1], ids[2]);    // senior → boss

        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[1], ids[0]), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AssignManagerErrors.ReportingCycle);
    }

    // ── other refusals ──────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_an_unknown_user_and_an_unknown_manager()
    {
        var ids = await SeedUsersAsync("a@sankore.ci");

        (await Assigner().Handle(new AssignManagerCommand(Guid.NewGuid(), ids[0]), CancellationToken.None))
            .Error.Should().Be(AssignManagerErrors.UserNotFound);

        (await Assigner().Handle(new AssignManagerCommand(ids[0], Guid.NewGuid()), CancellationToken.None))
            .Error.Should().Be(AssignManagerErrors.ManagerNotFound);
    }

    [Fact]
    public async Task Refuses_an_inactive_manager()
    {
        Guid userId;
        Guid disabledId;
        await using (var db = _factory.CreateContext())
        {
            var user = NewActiveUser("a@sankore.ci");
            var disabled = NewActiveUser("b@sankore.ci");
            disabled.Deactivate();
            db.Users.AddRange(user, disabled);
            await db.SaveChangesAsync();
            userId = user.Id;
            disabledId = disabled.Id;
        }

        var result = await Assigner().Handle(
            new AssignManagerCommand(userId, disabledId), CancellationToken.None);

        result.Error.Should().Be(AssignManagerErrors.ManagerNotActive);
    }

    [Fact]
    public async Task The_system_account_is_neither_managed_nor_a_manager()
    {
        Guid userId;
        Guid rootId;
        await using (var db = _factory.CreateContext())
        {
            var user = NewActiveUser("a@sankore.ci");
            var root = AppUser.CreateRoot(_tenantId, "Root", "Sankore", "root@sankore.ci");
            db.Users.AddRange(user, root);
            await db.SaveChangesAsync();
            userId = user.Id;
            rootId = root.Id;
        }

        (await Assigner().Handle(new AssignManagerCommand(rootId, userId), CancellationToken.None))
            .Error.Should().Be(AssignManagerErrors.SystemAccountImmutable);

        (await Assigner().Handle(new AssignManagerCommand(userId, rootId), CancellationToken.None))
            .Error.Should().Be(AssignManagerErrors.SystemAccountImmutable);
    }

    [Fact]
    public async Task A_manager_from_another_tenant_simply_does_not_exist()
    {
        var ids = await SeedUsersAsync("a@sankore.ci");
        var foreign = AppUser.Create(Guid.NewGuid(), _agencyId, "Ali", "Traore", "ali@other.ci");
        foreign.Activate();

        await using (var db = _factory.CreateContext())
        {
            db.Users.Add(foreign);
            await db.SaveChangesAsync();
        }

        var result = await Assigner().Handle(
            new AssignManagerCommand(ids[0], foreign.Id), CancellationToken.None);

        result.Error.Should().Be(AssignManagerErrors.ManagerNotFound);
    }

    // ── clearing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clearing_puts_a_user_at_the_top_without_touching_their_team()
    {
        var ids = await SeedUsersAsync("junior@sankore.ci", "senior@sankore.ci", "boss@sankore.ci");
        await LinkAsync(ids[0], ids[1]);    // junior → senior
        await LinkAsync(ids[1], ids[2]);    // senior → boss

        var result = await Clearer().Handle(new ClearManagerCommand(ids[1]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await ManagerOfAsync(ids[1])).Should().BeNull();
        (await ManagerOfAsync(ids[0])).Should().Be(ids[1],
            "clearing someone's own manager says nothing about their subordinates");
    }

    [Fact]
    public async Task Clearing_twice_is_harmless_and_an_unknown_user_is_reported()
    {
        var ids = await SeedUsersAsync("a@sankore.ci");

        (await Clearer().Handle(new ClearManagerCommand(ids[0]), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await Clearer().Handle(new ClearManagerCommand(ids[0]), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        (await Clearer().Handle(new ClearManagerCommand(Guid.NewGuid()), CancellationToken.None))
            .Error.Should().Be(AssignManagerErrors.UserNotFound);
    }
}
