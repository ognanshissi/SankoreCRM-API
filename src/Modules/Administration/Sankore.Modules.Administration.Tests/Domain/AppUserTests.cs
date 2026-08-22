namespace Sankore.Modules.Administration.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Xunit;

/// <summary>
/// Pure unit tests for the AppUser aggregate — state machine transitions
/// and lifecycle behaviour. No database or mocks required.
/// </summary>
public sealed class AppUserTests
{
    private static AppUser BuildUser(
        string name = "Awa Fall",
        string email = "awa@test.sn")
    {
        var tenantId = Guid.NewGuid();
        var agencyId = Guid.NewGuid();
        return AppUser.Create(tenantId, agencyId, name, email);
    }

    // ── Initial state ─────────────────────────────────────────────────────

    [Fact]
    public void New_user_starts_in_pending_activation_status()
    {
        var user = BuildUser();
        user.Status.Should().Be(UserStatus.PendingActivation);
        user.FailedLoginAttempts.Should().Be(0);
        user.MfaEnabled.Should().BeTrue();
        user.PasswordExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(89));
    }

    // ── Activate ─────────────────────────────────────────────────────────

    [Fact]
    public void Activate_transitions_status_to_active()
    {
        var user = BuildUser();
        user.Activate();
        user.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public void Cannot_activate_a_disabled_user()
    {
        var user = BuildUser();
        user.Activate();
        user.Deactivate();

        var act = () => user.Activate();
        act.Should().Throw<Sankore.Shared.Kernel.DomainException>()
            .WithMessage("*disabled*");
    }

    // ── RecordSuccessfulLogin ─────────────────────────────────────────────

    [Fact]
    public void First_login_transitions_pending_user_to_active()
    {
        var user = BuildUser();
        user.RecordSuccessfulLogin();
        user.Status.Should().Be(UserStatus.Active);
        user.FailedLoginAttempts.Should().Be(0);
        user.LastLoginAt.Should().NotBeNull();
    }

    // ── IncrementFailedLogin ──────────────────────────────────────────────

    [Fact]
    public void Account_is_locked_after_five_consecutive_failed_attempts()
    {
        var user = BuildUser();
        user.Activate();

        bool locked = false;
        for (var i = 0; i < 5; i++)
            locked = user.IncrementFailedLogin();

        locked.Should().BeTrue();
        user.Status.Should().Be(UserStatus.Locked);
        user.FailedLoginAttempts.Should().Be(5);
    }

    [Fact]
    public void Failed_login_before_threshold_does_not_lock_account()
    {
        var user = BuildUser();
        user.Activate();

        user.IncrementFailedLogin();
        user.IncrementFailedLogin();
        user.IncrementFailedLogin();

        user.Status.Should().Be(UserStatus.Active);
        user.FailedLoginAttempts.Should().Be(3);
    }

    // ── Deactivate ────────────────────────────────────────────────────────

    [Fact]
    public void Deactivate_sets_status_to_disabled_and_stamps_timestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var user = BuildUser();
        user.Activate();

        var evt = user.Deactivate();

        user.Status.Should().Be(UserStatus.Disabled);
        user.DeactivatedAt.Should().NotBeNull();
        user.DeactivatedAt!.Value.Should().BeAfter(before);
        user.IsAvailable.Should().BeFalse();

        evt.TenantId.Should().Be(user.TenantId);
        evt.UserId.Should().Be(user.Id);
    }

    [Fact]
    public void Deactivating_already_disabled_user_throws_domain_exception()
    {
        var user = BuildUser();
        user.Activate();
        user.Deactivate();

        var act = () => user.Deactivate();
        act.Should().Throw<Sankore.Shared.Kernel.DomainException>()
            .WithMessage("*already disabled*");
    }

    // ── ExtendPasswordExpiry ──────────────────────────────────────────────

    [Fact]
    public void Extend_password_expiry_pushes_date_forward_by_90_days()
    {
        var user = BuildUser();
        var before = DateTimeOffset.UtcNow.AddDays(89);

        user.ExtendPasswordExpiry();

        user.PasswordExpiresAt.Should().BeAfter(before);
        user.PasswordExpiresAt.Should().BeBefore(DateTimeOffset.UtcNow.AddDays(91));
    }
}
