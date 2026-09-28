namespace Sankore.Modules.Administration.Tests.Features.Agencies.AssignAgencyManager;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

public sealed class AssignAgencyManagerHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public AssignAgencyManagerHandlerTests() => _factory = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private Agency NewAgency(string code = "AG000001") =>
        Agency.Create(_tenantId, code, "Agence Plateau", "", AgencyType.HeadQuarter, null, null);

    private AppUser NewActiveUser(Guid agencyId, string email = "awa@sankore.ci")
    {
        var user = AppUser.Create(_tenantId, agencyId, "Awa", "Ouattara", email);
        user.Activate();
        return user;
    }

    private async Task<(Agency Agency, AppUser Manager)> SeedAgencyWithCandidateAsync()
    {
        await using var db = _factory.CreateContext();
        var agency = NewAgency();
        db.Agencies.Add(agency);
        var user = NewActiveUser(agency.Id);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (agency, user);
    }

    private readonly AppRole _branchManagerRole =
        AppRole.Create(Sankore.Shared.Kernel.Roles.BranchManager.Code, "Compte Branch Manager", isSystem: true);

    /// <summary>
    /// Identity managers are NSubstitute mocks: the role write has to go through UserManager
    /// because login reads its claims from there, but no real store is needed to prove the
    /// coordinator calls it — and calls it only when it should.
    /// </summary>
    private (AgencyManagerRoleCoordinator Coordinator, UserManager<AppUser> Users) BuildRoles(
        AdministrationDbContext db)
    {
        var userManager = IdentityMockFactory.BuildUserManager();
        userManager.AddToRoleAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        userManager.RemoveFromRoleAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);

        var roleManager = IdentityMockFactory.BuildRoleManager();
        roleManager.FindByNameAsync(Sankore.Shared.Kernel.Roles.BranchManager.Code)
            .Returns(_branchManagerRole);

        return (new AgencyManagerRoleCoordinator(db, userManager, roleManager), userManager);
    }

    /// <summary>Every queued email, in order, so tests can assert on what was sent.</summary>
    private readonly List<QueueEmailRequest> _sentEmails = [];

    private INotificationsModule BuildNotifications()
    {
        var notifications = Substitute.For<INotificationsModule>();
        notifications
            .QueueEmailAsync(Arg.Do<QueueEmailRequest>(_sentEmails.Add), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(Guid.NewGuid()));
        return notifications;
    }

    private ITenantStore BuildTenantStore()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Sankore MFI", "sankore.ci", true, false, null, null, "fr"));
        return store;
    }

    private AgencyManagerNotifier BuildNotifier(AdministrationDbContext db, INotificationsModule? notifications = null)
        => new(
            db,
            new ModuleEmailSender(
                notifications ?? BuildNotifications(),
                BuildTenantStore(),
                NullLogger<ModuleEmailSender>.Instance),
            NullLogger<AgencyManagerNotifier>.Instance);

    private AssignAgencyManagerHandler Assigner()
    {
        var db = _factory.CreateContext();
        return new AssignAgencyManagerHandler(db, BuildRoles(db).Coordinator, BuildNotifier(db));
    }

    private RemoveAgencyManagerHandler Remover()
    {
        var db = _factory.CreateContext();
        return new RemoveAgencyManagerHandler(db, BuildRoles(db).Coordinator, BuildNotifier(db));
    }

    private async Task<UserRole?> ActiveBranchManagerGrantAsync(Guid userId)
    {
        await using var db = _factory.CreateContext();
        return db.UserRoles.FirstOrDefault(
            ur => ur.UserId == userId && ur.RoleId == _branchManagerRole.Id && ur.IsActive);
    }

    private async Task<Agency> ReloadAsync(Guid agencyId)
    {
        await using var db = _factory.CreateContext();
        return db.Agencies.Single(a => a.Id == agencyId);
    }

    // ── assigning ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Assigns_an_active_user_of_the_agency()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ManagerUserId.Should().Be(manager.Id);
        result.Value.ManagerFullName.Should().Be("Awa Ouattara");
        result.Value.PreviousManagerUserId.Should().BeNull();
        result.Value.Changed.Should().BeTrue();
        result.Value.GrantedRole.Should().Be(Sankore.Shared.Kernel.Roles.BranchManager.Code);

        (await ReloadAsync(agency.Id)).ManagerUserId.Should().Be(manager.Id);
    }

    [Fact]
    public async Task Reports_the_previous_holder_when_the_post_changes_hands()
    {
        var (agency, first) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, first.Id), CancellationToken.None);

        Guid secondId;
        await using (var db = _factory.CreateContext())
        {
            var second = NewActiveUser(agency.Id, "koffi@sankore.ci");
            db.Users.Add(second);
            await db.SaveChangesAsync();
            secondId = second.Id;
        }

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(agency.Id, secondId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PreviousManagerUserId.Should().Be(first.Id);
        result.Value.ManagerUserId.Should().Be(secondId);
        (await ReloadAsync(agency.Id)).ManagerUserId.Should().Be(secondId);
    }

    [Fact]
    public async Task Re_assigning_the_same_manager_succeeds_without_changing_anything()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);
        var stampAfterFirst = (await ReloadAsync(agency.Id)).UpdatedAt;

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Changed.Should().BeFalse("a retried request must not churn the agency");
        (await ReloadAsync(agency.Id)).UpdatedAt.Should().Be(stampAfterFirst);
    }

    [Fact]
    public async Task Accepts_a_super_user_who_belongs_to_no_agency()
    {
        Guid agencyId;
        Guid rootId;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency();
            db.Agencies.Add(agency);
            var root = AppUser.CreateRoot(_tenantId, "Root", "Sankore", "root@sankore.ci");
            db.Users.Add(root);
            await db.SaveChangesAsync();
            agencyId = agency.Id;
            rootId = root.Id;
        }

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(agencyId, rootId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "a super-user operates across the tenant and has no agency of their own");
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_an_unknown_agency()
    {
        var (_, manager) = await SeedAgencyWithCandidateAsync();

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(Guid.NewGuid(), manager.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AgencyManagerErrors.AgencyNotFound);
    }

    [Fact]
    public async Task Refuses_a_deleted_agency()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await using (var db = _factory.CreateContext())
        {
            var tracked = db.Agencies.AsTracking().Single(a => a.Id == agency.Id);
            tracked.Deactivate();
            await db.SaveChangesAsync();
        }

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AgencyManagerErrors.AgencyDeleted);
    }

    [Fact]
    public async Task Refuses_an_unknown_user()
    {
        var (agency, _) = await SeedAgencyWithCandidateAsync();

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(agency.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AgencyManagerErrors.ManagerNotFound);
    }

    [Theory]
    [InlineData(UserStatus.PendingActivation)]
    [InlineData(UserStatus.Disabled)]
    public async Task Refuses_a_user_who_is_not_active(UserStatus status)
    {
        Guid agencyId;
        Guid userId;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency();
            db.Agencies.Add(agency);

            var user = AppUser.Create(_tenantId, agency.Id, "Koffi", "Kone", "koffi@sankore.ci");
            if (status == UserStatus.Disabled)
            {
                user.Activate();
                user.Deactivate();
            }

            db.Users.Add(user);
            await db.SaveChangesAsync();
            agencyId = agency.Id;
            userId = user.Id;
        }

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(agencyId, userId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AgencyManagerErrors.ManagerNotActive);
    }

    [Fact]
    public async Task Refuses_a_user_who_belongs_to_another_agency()
    {
        Guid targetAgencyId;
        Guid outsiderId;
        await using (var db = _factory.CreateContext())
        {
            var target = NewAgency("AG000001");
            var other = NewAgency("AG000002");
            db.Agencies.AddRange(target, other);

            var outsider = NewActiveUser(other.Id, "outsider@sankore.ci");
            db.Users.Add(outsider);
            await db.SaveChangesAsync();
            targetAgencyId = target.Id;
            outsiderId = outsider.Id;
        }

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(targetAgencyId, outsiderId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AgencyManagerErrors.ManagerNotInAgency);
    }

    [Fact]
    public async Task A_user_of_another_tenant_simply_does_not_exist()
    {
        var (agency, _) = await SeedAgencyWithCandidateAsync();

        // Same in-memory database, different tenant context: the query filter must hide the row
        // rather than let another tenant's employee be put in charge of this agency.
        var otherTenantId = Guid.NewGuid();
        var foreignUser = AppUser.Create(otherTenantId, agency.Id, "Ali", "Traore", "ali@other.ci");
        foreignUser.Activate();

        await using (var db = _factory.CreateContext())
        {
            db.Users.Add(foreignUser);
            await db.SaveChangesAsync();
        }

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(agency.Id, foreignUser.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AgencyManagerErrors.ManagerNotFound);
    }

    // ── the BranchManager role follows the post ─────────────────────────────

    [Fact]
    public async Task Grants_the_branch_manager_role_to_the_new_manager()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();

        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        var grant = await ActiveBranchManagerGrantAsync(manager.Id);
        grant.Should().NotBeNull();
        grant!.AssignedBy.Should().Be(AgencyManagerRoleCoordinator.SystemGrant,
            "the grant is a side effect of the assignment, not an administrator's deliberate act");
    }

    [Fact]
    public async Task Does_not_stack_a_second_grant_when_the_role_is_already_held()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        await using var db = _factory.CreateContext();
        db.UserRoles.Count(ur => ur.UserId == manager.Id && ur.IsActive).Should().Be(1);
    }

    [Fact]
    public async Task Takes_the_role_back_from_the_outgoing_manager()
    {
        var (agency, first) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, first.Id), CancellationToken.None);

        Guid secondId;
        await using (var db = _factory.CreateContext())
        {
            var second = NewActiveUser(agency.Id, "koffi@sankore.ci");
            db.Users.Add(second);
            await db.SaveChangesAsync();
            secondId = second.Id;
        }

        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, secondId), CancellationToken.None);

        (await ActiveBranchManagerGrantAsync(first.Id)).Should().BeNull();
        (await ActiveBranchManagerGrantAsync(secondId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Keeps_the_role_when_the_outgoing_manager_still_runs_another_agency()
    {
        // Only a super-user can run two agencies: AppUser.AgencyId is single-valued, so the
        // "must belong to the agency" rule confines an ordinary employee to one. This is the
        // case that makes the coordinator's "still manages something" guard load-bearing.
        Guid firstAgencyId;
        Guid secondAgencyId;
        Guid rootId;
        Guid successorId;
        await using (var db = _factory.CreateContext())
        {
            var first = NewAgency("AG000001");
            var second = NewAgency("AG000002");
            db.Agencies.AddRange(first, second);

            var root = AppUser.CreateRoot(_tenantId, "Root", "Sankore", "root@sankore.ci");
            var successor = NewActiveUser(first.Id, "koffi@sankore.ci");
            db.Users.AddRange(root, successor);
            await db.SaveChangesAsync();

            firstAgencyId = first.Id;
            secondAgencyId = second.Id;
            rootId = root.Id;
            successorId = successor.Id;
        }

        (await Assigner().Handle(new AssignAgencyManagerCommand(firstAgencyId, rootId), CancellationToken.None))
            .IsSuccess.Should().BeTrue();
        (await Assigner().Handle(new AssignAgencyManagerCommand(secondAgencyId, rootId), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        // Handing the first agency over must not strip a role the second one still justifies.
        (await Assigner().Handle(new AssignAgencyManagerCommand(firstAgencyId, successorId), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        (await ActiveBranchManagerGrantAsync(rootId)).Should().NotBeNull(
            "they still run the second agency");
        (await ActiveBranchManagerGrantAsync(successorId)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_employee_cannot_run_a_second_agency_because_they_belong_to_one()
    {
        // Pins the consequence of the eligibility rule, so the day someone wants shared
        // management of several branches this test is what tells them what has to change.
        Guid otherAgencyId;
        Guid managerId;
        await using (var db = _factory.CreateContext())
        {
            var home = NewAgency("AG000001");
            var other = NewAgency("AG000002");
            db.Agencies.AddRange(home, other);
            var manager = NewActiveUser(home.Id);
            db.Users.Add(manager);
            await db.SaveChangesAsync();
            otherAgencyId = other.Id;
            managerId = manager.Id;
        }

        var result = await Assigner().Handle(
            new AssignAgencyManagerCommand(otherAgencyId, managerId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AgencyManagerErrors.ManagerNotInAgency);
    }

    [Fact]
    public async Task Never_revokes_a_role_an_administrator_granted_deliberately()
    {
        var (agency, first) = await SeedAgencyWithCandidateAsync();
        var administratorId = Guid.NewGuid();

        // Granted by hand through the roles endpoint, before any agency was involved.
        await using (var db = _factory.CreateContext())
        {
            db.UserRoles.Add(UserRole.Assign(_tenantId, first.Id, _branchManagerRole.Id, administratorId));
            await db.SaveChangesAsync();
        }

        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, first.Id), CancellationToken.None);
        await Remover().Handle(new RemoveAgencyManagerCommand(agency.Id), CancellationToken.None);

        var grant = await ActiveBranchManagerGrantAsync(first.Id);
        grant.Should().NotBeNull("an explicit grant must outlive the agency that happened to need it");
        grant!.AssignedBy.Should().Be(administratorId);
    }

    [Fact]
    public async Task Takes_the_role_back_when_the_post_is_left_vacant()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        await Remover().Handle(new RemoveAgencyManagerCommand(agency.Id), CancellationToken.None);

        (await ActiveBranchManagerGrantAsync(manager.Id)).Should().BeNull();
    }

    // ── notifying the new manager ───────────────────────────────────────────

    [Fact]
    public async Task Emails_the_new_manager_about_the_agency_they_now_run()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();

        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        _sentEmails.Should().ContainSingle();
        var mail = _sentEmails[0];
        mail.TemplateKey.Should().Be("agency.manager-assigned");
        mail.RecipientEmail.Should().Be("awa@sankore.ci");
        mail.RecipientName.Should().Be("Awa Ouattara");
        mail.Module.Should().Be("Administration");
        mail.TenantId.Should().Be(_tenantId);
        mail.TemplateData["agency_name"].Should().Be("Agence Plateau");
        mail.TemplateData["agency_code"].Should().Be("AG000001");
        mail.TemplateData["company_name"].Should().Be("Sankore MFI");
        mail.TemplateData["role_name"].Should().Be(Sankore.Shared.Kernel.Roles.BranchManager.Code);
    }

    [Fact]
    public async Task Does_not_email_again_when_the_same_manager_is_re_assigned()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        _sentEmails.Should().ContainSingle("a retried request must not mail the same person twice");
    }

    [Fact]
    public async Task Emails_the_successor_but_not_the_outgoing_manager()
    {
        var (agency, first) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, first.Id), CancellationToken.None);

        Guid secondId;
        await using (var db = _factory.CreateContext())
        {
            var second = NewActiveUser(agency.Id, "koffi@sankore.ci");
            db.Users.Add(second);
            await db.SaveChangesAsync();
            secondId = second.Id;
        }

        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, secondId), CancellationToken.None);

        // First assignment: one mail. Handover: one to the successor, one to the outgoing manager.
        _sentEmails.Should().HaveCount(3);
        _sentEmails[1].TemplateKey.Should().Be(AgencyManagerNotifier.AssignedTemplate);
        _sentEmails[1].RecipientEmail.Should().Be("koffi@sankore.ci");
        _sentEmails[2].TemplateKey.Should().Be(AgencyManagerNotifier.UnassignedTemplate);
        _sentEmails[2].RecipientEmail.Should().Be("awa@sankore.ci");
        _sentEmails.Select(m => m.IdempotencyKey).Should().OnlyHaveUniqueItems(
            "two genuine assignments must not collapse into one outbox row");
    }

    [Fact]
    public async Task Honours_the_manager_preferred_language()
    {
        Guid agencyId;
        Guid managerId;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency();
            db.Agencies.Add(agency);
            var manager = NewActiveUser(agency.Id);
            manager.SetPreferredLanguage("en");
            db.Users.Add(manager);
            await db.SaveChangesAsync();
            agencyId = agency.Id;
            managerId = manager.Id;
        }

        await Assigner().Handle(new AssignAgencyManagerCommand(agencyId, managerId), CancellationToken.None);

        _sentEmails.Should().ContainSingle();
        _sentEmails[0].Locale.Should().Be("en");
    }

    [Fact]
    public async Task A_failing_notification_never_undoes_the_assignment()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();

        var db = _factory.CreateContext();
        var notifications = Substitute.For<INotificationsModule>();
        notifications
            .QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<Guid>>>(_ => throw new InvalidOperationException("SMTP is down"));

        var handler = new AssignAgencyManagerHandler(
            db, BuildRoles(db).Coordinator, BuildNotifier(db, notifications));

        var result = await handler.Handle(
            new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "the administrative decision is taken and saved before the mail is queued");
        (await ReloadAsync(agency.Id)).ManagerUserId.Should().Be(manager.Id);
    }

    [Fact]
    public async Task Emails_the_outgoing_manager_when_the_post_is_left_vacant()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);
        _sentEmails.Clear();

        await Remover().Handle(new RemoveAgencyManagerCommand(agency.Id), CancellationToken.None);

        _sentEmails.Should().ContainSingle();
        var mail = _sentEmails[0];
        mail.TemplateKey.Should().Be(AgencyManagerNotifier.UnassignedTemplate);
        mail.RecipientEmail.Should().Be("awa@sankore.ci");
        mail.TemplateData["agency_name"].Should().Be("Agence Plateau");
        mail.TemplateData.Should().NotContainKey("role_name",
            "the outgoing manager may keep the role if they still run another agency");
    }

    [Fact]
    public async Task Removing_a_manager_twice_emails_only_once()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);
        await Remover().Handle(new RemoveAgencyManagerCommand(agency.Id), CancellationToken.None);
        _sentEmails.Clear();

        await Remover().Handle(new RemoveAgencyManagerCommand(agency.Id), CancellationToken.None);

        _sentEmails.Should().BeEmpty("there is nobody left to tell");
    }

    [Fact]
    public async Task Honours_the_outgoing_manager_preferred_language()
    {
        Guid agencyId;
        Guid managerId;
        await using (var db = _factory.CreateContext())
        {
            var agency = NewAgency();
            db.Agencies.Add(agency);
            var manager = NewActiveUser(agency.Id);
            manager.SetPreferredLanguage("en");
            db.Users.Add(manager);
            await db.SaveChangesAsync();
            agencyId = agency.Id;
            managerId = manager.Id;
        }

        await Assigner().Handle(new AssignAgencyManagerCommand(agencyId, managerId), CancellationToken.None);
        _sentEmails.Clear();

        await Remover().Handle(new RemoveAgencyManagerCommand(agencyId), CancellationToken.None);

        _sentEmails.Should().ContainSingle();
        _sentEmails[0].Locale.Should().Be("en");
    }

    // ── removing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Leaves_the_post_vacant()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);

        var result = await Remover().Handle(
            new RemoveAgencyManagerCommand(agency.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await ReloadAsync(agency.Id)).ManagerUserId.Should().BeNull();
    }

    [Fact]
    public async Task Removing_a_manager_twice_is_harmless()
    {
        var (agency, manager) = await SeedAgencyWithCandidateAsync();
        await Assigner().Handle(new AssignAgencyManagerCommand(agency.Id, manager.Id), CancellationToken.None);
        await Remover().Handle(new RemoveAgencyManagerCommand(agency.Id), CancellationToken.None);

        var result = await Remover().Handle(
            new RemoveAgencyManagerCommand(agency.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await ReloadAsync(agency.Id)).ManagerUserId.Should().BeNull();
    }

    [Fact]
    public async Task Removing_from_an_unknown_agency_is_reported()
    {
        var result = await Remover().Handle(
            new RemoveAgencyManagerCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(AgencyManagerErrors.AgencyNotFound);
    }
}
