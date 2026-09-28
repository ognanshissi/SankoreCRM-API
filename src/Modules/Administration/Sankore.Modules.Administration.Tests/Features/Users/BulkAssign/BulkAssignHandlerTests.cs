namespace Sankore.Modules.Administration.Tests.Features.Users.BulkAssign;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.BulkAssign;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class BulkAssignHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _operatorId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ICurrentUser _currentUser;

    private readonly AppRole _role =
        AppRole.Create(Sankore.Shared.Kernel.Roles.Agent.Code, "Compte Agent", isSystem: true);

    public BulkAssignHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
        _currentUser.Id.Returns(_operatorId);
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private Agency NewAgency(string code) =>
        Agency.Create(_tenantId, code, $"Agence {code}", "", AgencyType.HeadQuarter, null, null);

    private AppUser NewActiveUser(Guid agencyId, string email)
    {
        var user = AppUser.Create(_tenantId, agencyId, "Awa", "Ouattara", email);
        user.Activate();
        return user;
    }

    /// <summary>Every queued email, in order, so tests can assert on who was told what.</summary>
    private readonly List<QueueEmailRequest> _sentEmails = [];

    private BulkAssignNotifier BuildNotifier(INotificationsModule? notifications = null)
    {
        if (notifications is null)
        {
            notifications = Substitute.For<INotificationsModule>();
            notifications
                .QueueEmailAsync(Arg.Do<QueueEmailRequest>(_sentEmails.Add), Arg.Any<CancellationToken>())
                .Returns(Result.Ok(Guid.NewGuid()));
        }

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Sankore MFI", "sankore.ci", true, false, null, null, "fr"));

        return new BulkAssignNotifier(
            new ModuleEmailSender(notifications, tenantStore, NullLogger<ModuleEmailSender>.Instance));
    }

    private BulkAssignAgencyHandler AgencyHandler()
        => new(_factory.CreateContext(), BuildNotifier());

    private BulkAssignRoleHandler RoleHandler()
    {
        var userManager = IdentityMockFactory.BuildUserManager();
        userManager.AddToRoleAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);

        var roleManager = IdentityMockFactory.BuildRoleManager();
        roleManager.FindByIdAsync(_role.Id.ToString()).Returns(_role);

        return new BulkAssignRoleHandler(
            _factory.CreateContext(), userManager, roleManager, _currentUser, BuildNotifier());
    }

    private async Task<AppUser> ReloadUserAsync(Guid userId)
    {
        await using var db = _factory.CreateContext();
        return db.Users.Single(u => u.Id == userId);
    }

    // ── assigning an agency ─────────────────────────────────────────────────

    [Fact]
    public async Task Moves_every_selected_user_into_the_agency()
    {
        Guid targetId;
        List<Guid> userIds;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);

            var users = new[]
            {
                NewActiveUser(home.Id, "a@sankore.ci"),
                NewActiveUser(home.Id, "b@sankore.ci"),
                NewActiveUser(home.Id, "c@sankore.ci"),
            };
            db.Users.AddRange(users);
            await db.SaveChangesAsync();

            targetId = target.Id;
            userIds = users.Select(u => u.Id).ToList();
        }

        var result = await AgencyHandler().Handle(
            new BulkAssignAgencyCommand(userIds, targetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Requested.Should().Be(3);
        result.Value.Applied.Should().Be(3);
        result.Value.Skipped.Should().Be(0);

        foreach (var id in userIds)
            (await ReloadUserAsync(id)).AgencyId.Should().Be(targetId);
    }

    [Fact]
    public async Task Applies_the_eligible_users_and_reports_the_others()
    {
        Guid targetId;
        Guid movableId;
        Guid alreadyThereId;
        var unknownId = Guid.NewGuid();
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);

            var movable = NewActiveUser(home.Id, "a@sankore.ci");
            var alreadyThere = NewActiveUser(target.Id, "b@sankore.ci");
            db.Users.AddRange(movable, alreadyThere);
            await db.SaveChangesAsync();

            targetId = target.Id;
            movableId = movable.Id;
            alreadyThereId = alreadyThere.Id;
        }

        var result = await AgencyHandler().Handle(
            new BulkAssignAgencyCommand([movableId, alreadyThereId, unknownId], targetId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue("one ineligible row must not discard the whole selection");
        result.Value.Applied.Should().Be(1);
        result.Value.Skipped.Should().Be(2);

        result.Value.Outcomes.Single(o => o.UserId == movableId).Applied.Should().BeTrue();
        result.Value.Outcomes.Single(o => o.UserId == alreadyThereId).Reason
            .Should().Be(BulkAssignReasons.AlreadyInAgency);
        result.Value.Outcomes.Single(o => o.UserId == unknownId).Reason
            .Should().Be(BulkAssignReasons.UserNotFound);

        (await ReloadUserAsync(movableId)).AgencyId.Should().Be(targetId,
            "the users that did apply are committed");
    }

    [Fact]
    public async Task Refuses_to_move_a_user_who_manages_another_agency()
    {
        Guid targetId;
        Guid managerId;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);

            var manager = NewActiveUser(home.Id, "manager@sankore.ci");
            db.Users.Add(manager);
            await db.SaveChangesAsync();

            home.AssignManager(manager.Id);
            await db.SaveChangesAsync();

            targetId = target.Id;
            managerId = manager.Id;
        }

        var result = await AgencyHandler().Handle(
            new BulkAssignAgencyCommand([managerId], targetId), CancellationToken.None);

        result.Value.Applied.Should().Be(0);
        result.Value.Outcomes.Single().Reason.Should().Be(BulkAssignReasons.UserManagesAnAgency);

        (await ReloadUserAsync(managerId)).AgencyId.Should().NotBe(targetId,
            "an agency must never be left with a manager who does not belong to it");
    }

    [Fact]
    public async Task Never_moves_the_system_account()
    {
        Guid targetId;
        Guid rootId;
        await using (var db = _factory.CreateContext())
        {
            var target = NewAgency("AG000002");
            db.Agencies.Add(target);
            var root = AppUser.CreateRoot(_tenantId, "Root", "Sankore", "root@sankore.ci");
            db.Users.Add(root);
            await db.SaveChangesAsync();
            targetId = target.Id;
            rootId = root.Id;
        }

        var result = await AgencyHandler().Handle(
            new BulkAssignAgencyCommand([rootId], targetId), CancellationToken.None);

        result.Value.Outcomes.Single().Reason.Should().Be(BulkAssignReasons.SystemAccountImmutable);
        (await ReloadUserAsync(rootId)).AgencyId.Should().BeNull();
    }

    [Fact]
    public async Task The_same_user_selected_twice_counts_once()
    {
        Guid targetId;
        Guid userId;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);
            var user = NewActiveUser(home.Id, "a@sankore.ci");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            targetId = target.Id;
            userId = user.Id;
        }

        var result = await AgencyHandler().Handle(
            new BulkAssignAgencyCommand([userId, userId], targetId), CancellationToken.None);

        result.Value.Requested.Should().Be(1);
        result.Value.Outcomes.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unknown_agency_fails_the_whole_request()
    {
        var result = await AgencyHandler().Handle(
            new BulkAssignAgencyCommand([Guid.NewGuid()], Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue("the target is common to every user, so nothing can apply");
        result.Error.Should().Be(BulkAssignErrors.AgencyNotFound);
    }

    // ── telling the users who moved ─────────────────────────────────────────

    [Fact]
    public async Task Emails_only_the_users_who_actually_moved()
    {
        Guid targetId;
        Guid movableId;
        Guid alreadyThereId;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);

            var movable = NewActiveUser(home.Id, "moved@sankore.ci");
            var alreadyThere = NewActiveUser(target.Id, "stays@sankore.ci");
            db.Users.AddRange(movable, alreadyThere);
            await db.SaveChangesAsync();

            targetId = target.Id;
            movableId = movable.Id;
            alreadyThereId = alreadyThere.Id;
        }

        await AgencyHandler().Handle(
            new BulkAssignAgencyCommand([movableId, alreadyThereId, Guid.NewGuid()], targetId),
            CancellationToken.None);

        _sentEmails.Should().ContainSingle("a skipped row is not a change worth an email");
        var mail = _sentEmails[0];
        mail.TemplateKey.Should().Be(BulkAssignNotifier.AgencyChangedTemplate);
        mail.RecipientEmail.Should().Be("moved@sankore.ci");
        mail.TenantId.Should().Be(_tenantId);
        mail.TemplateData["agency_name"].Should().Be("Agence AG000002");
        mail.TemplateData["agency_code"].Should().Be("AG000002");
        mail.TemplateData["company_name"].Should().Be("Sankore MFI");
    }

    [Fact]
    public async Task Emails_every_moved_user_with_its_own_idempotency_key()
    {
        Guid targetId;
        List<Guid> userIds;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);
            var users = new[]
            {
                NewActiveUser(home.Id, "a@sankore.ci"),
                NewActiveUser(home.Id, "b@sankore.ci"),
            };
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            targetId = target.Id;
            userIds = users.Select(u => u.Id).ToList();
        }

        await AgencyHandler().Handle(
            new BulkAssignAgencyCommand(userIds, targetId), CancellationToken.None);

        _sentEmails.Should().HaveCount(2);
        _sentEmails.Select(m => m.IdempotencyKey).Should().OnlyHaveUniqueItems(
            "one outbox row per recipient, or the second user never hears about it");
    }

    [Fact]
    public async Task A_replayed_request_does_not_email_the_same_user_twice()
    {
        Guid targetId;
        Guid userId;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);
            var user = NewActiveUser(home.Id, "a@sankore.ci");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            targetId = target.Id;
            userId = user.Id;
        }

        await AgencyHandler().Handle(new BulkAssignAgencyCommand([userId], targetId), CancellationToken.None);
        _sentEmails.Clear();

        // Second time round the user is already there, so the row is skipped — and silence
        // follows from the skip rather than from any extra de-duplication.
        await AgencyHandler().Handle(new BulkAssignAgencyCommand([userId], targetId), CancellationToken.None);

        _sentEmails.Should().BeEmpty();
    }

    [Fact]
    public async Task Honours_each_moved_user_preferred_language()
    {
        Guid targetId;
        Guid userId;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);
            var user = NewActiveUser(home.Id, "a@sankore.ci");
            user.SetPreferredLanguage("en");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            targetId = target.Id;
            userId = user.Id;
        }

        await AgencyHandler().Handle(new BulkAssignAgencyCommand([userId], targetId), CancellationToken.None);

        _sentEmails.Should().ContainSingle();
        _sentEmails[0].Locale.Should().Be("en");
    }

    [Fact]
    public async Task A_failing_notification_never_undoes_the_move()
    {
        Guid targetId;
        Guid userId;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var target = NewAgency("AG000002");
            db.Agencies.AddRange(home, target);
            var user = NewActiveUser(home.Id, "a@sankore.ci");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            targetId = target.Id;
            userId = user.Id;
        }

        var notifications = Substitute.For<INotificationsModule>();
        notifications
            .QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<Guid>>>(_ => throw new InvalidOperationException("SMTP is down"));

        var handler = new BulkAssignAgencyHandler(
            _factory.CreateContext(), BuildNotifier(notifications));

        var result = await handler.Handle(
            new BulkAssignAgencyCommand([userId], targetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Applied.Should().Be(1);
        (await ReloadUserAsync(userId)).AgencyId.Should().Be(targetId,
            "the move is committed before anything is queued");
    }

    // ── assigning a role ────────────────────────────────────────────────────

    [Fact]
    public async Task Grants_the_role_to_every_selected_user_and_records_the_operator()
    {
        List<Guid> userIds;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency("AG000001");
            db.Agencies.Add(agency);
            var users = new[]
            {
                NewActiveUser(agency.Id, "a@sankore.ci"),
                NewActiveUser(agency.Id, "b@sankore.ci"),
            };
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            userIds = users.Select(u => u.Id).ToList();
        }

        var result = await RoleHandler().Handle(
            new BulkAssignRoleCommand(userIds, _role.Id), CancellationToken.None);

        result.Value.Applied.Should().Be(2);

        await using var check = _factory.CreateContext();
        var grants = check.UserRoles.Where(ur => ur.RoleId == _role.Id && ur.IsActive).ToList();
        grants.Should().HaveCount(2);
        grants.Should().OnlyContain(g => g.AssignedBy == _operatorId,
            "a deliberate grant must not be mistaken for an automatic one and revoked later");
    }

    [Fact]
    public async Task Skips_a_user_who_already_holds_the_role()
    {
        Guid holderId;
        Guid newcomerId;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency("AG000001");
            db.Agencies.Add(agency);
            var holder = NewActiveUser(agency.Id, "a@sankore.ci");
            var newcomer = NewActiveUser(agency.Id, "b@sankore.ci");
            db.Users.AddRange(holder, newcomer);
            db.UserRoles.Add(UserRole.Assign(_tenantId, holder.Id, _role.Id, _operatorId));
            await db.SaveChangesAsync();
            holderId = holder.Id;
            newcomerId = newcomer.Id;
        }

        var result = await RoleHandler().Handle(
            new BulkAssignRoleCommand([holderId, newcomerId], _role.Id), CancellationToken.None);

        result.Value.Applied.Should().Be(1);
        result.Value.Outcomes.Single(o => o.UserId == holderId).Reason
            .Should().Be(BulkAssignReasons.AlreadyHasRole);

        await using var check = _factory.CreateContext();
        check.UserRoles.Count(ur => ur.UserId == holderId && ur.IsActive).Should().Be(1,
            "re-granting must not stack a second active row");
    }

    [Fact]
    public async Task An_unknown_role_fails_the_whole_request()
    {
        var result = await RoleHandler().Handle(
            new BulkAssignRoleCommand([Guid.NewGuid()], Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(BulkAssignErrors.RoleNotFound);
    }

    [Fact]
    public async Task A_user_of_another_tenant_is_simply_not_found()
    {
        Guid targetId;
        var foreignUser = AppUser.Create(Guid.NewGuid(), Guid.NewGuid(), "Ali", "Traore", "ali@other.ci");
        foreignUser.Activate();

        await using (var db = _factory.CreateContext())
        {
            var target = NewAgency("AG000002");
            db.Agencies.Add(target);
            db.Users.Add(foreignUser);
            await db.SaveChangesAsync();
            targetId = target.Id;
        }

        var result = await AgencyHandler().Handle(
            new BulkAssignAgencyCommand([foreignUser.Id], targetId), CancellationToken.None);

        result.Value.Outcomes.Single().Reason.Should().Be(BulkAssignReasons.UserNotFound);
    }

    [Fact]
    public async Task Emails_only_the_users_who_actually_gained_the_role()
    {
        Guid holderId;
        Guid newcomerId;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency("AG000001");
            db.Agencies.Add(agency);
            var holder = NewActiveUser(agency.Id, "holder@sankore.ci");
            var newcomer = NewActiveUser(agency.Id, "newcomer@sankore.ci");
            db.Users.AddRange(holder, newcomer);
            db.UserRoles.Add(UserRole.Assign(_tenantId, holder.Id, _role.Id, _operatorId));
            await db.SaveChangesAsync();
            holderId = holder.Id;
            newcomerId = newcomer.Id;
        }

        await RoleHandler().Handle(
            new BulkAssignRoleCommand([holderId, newcomerId, Guid.NewGuid()], _role.Id),
            CancellationToken.None);

        _sentEmails.Should().ContainSingle("someone who already held the role learned nothing new");
        var mail = _sentEmails[0];
        mail.TemplateKey.Should().Be(BulkAssignNotifier.RoleGrantedTemplate);
        mail.RecipientEmail.Should().Be("newcomer@sankore.ci");
        mail.TenantId.Should().Be(_tenantId);
        mail.TemplateData["role_name"].Should().Be("Compte Agent",
            "the label is what a human recognises, not the policy code");
        mail.TemplateData["company_name"].Should().Be("Sankore MFI");
    }

    [Fact]
    public async Task Emails_every_user_granted_the_role_with_its_own_idempotency_key()
    {
        List<Guid> userIds;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency("AG000001");
            db.Agencies.Add(agency);
            var users = new[]
            {
                NewActiveUser(agency.Id, "a@sankore.ci"),
                NewActiveUser(agency.Id, "b@sankore.ci"),
            };
            db.Users.AddRange(users);
            await db.SaveChangesAsync();
            userIds = users.Select(u => u.Id).ToList();
        }

        await RoleHandler().Handle(
            new BulkAssignRoleCommand(userIds, _role.Id), CancellationToken.None);

        _sentEmails.Should().HaveCount(2);
        _sentEmails.Select(m => m.IdempotencyKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_replayed_role_grant_does_not_email_the_same_user_twice()
    {
        Guid userId;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency("AG000001");
            db.Agencies.Add(agency);
            var user = NewActiveUser(agency.Id, "a@sankore.ci");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        await RoleHandler().Handle(new BulkAssignRoleCommand([userId], _role.Id), CancellationToken.None);
        _sentEmails.Clear();

        await RoleHandler().Handle(new BulkAssignRoleCommand([userId], _role.Id), CancellationToken.None);

        _sentEmails.Should().BeEmpty("the second pass skips a user who already holds the role");
    }

    [Fact]
    public async Task Honours_the_preferred_language_of_a_user_granted_a_role()
    {
        Guid userId;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency("AG000001");
            db.Agencies.Add(agency);
            var user = NewActiveUser(agency.Id, "a@sankore.ci");
            user.SetPreferredLanguage("en");
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        await RoleHandler().Handle(new BulkAssignRoleCommand([userId], _role.Id), CancellationToken.None);

        _sentEmails.Should().ContainSingle();
        _sentEmails[0].Locale.Should().Be("en");
    }
}
