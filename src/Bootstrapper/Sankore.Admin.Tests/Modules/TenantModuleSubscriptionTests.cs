namespace Sankore.Admin.Tests.Modules;

using FluentAssertions;
using Sankore.Admin.Domain;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Every transition of a subscription, valid and invalid. A false is a conflict, never a throw.</summary>
public sealed class TenantModuleSubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private static TenantModuleSubscription Requested() => TenantModuleSubscription.Request(
        Tenant, TenantModules.Insurance, User, "admin@caisse.ci", 150000m, "xof", Now);

    [Fact]
    public void A_request_records_who_asked_and_the_price_they_accepted()
    {
        var row = Requested();

        row.Status.Should().Be(ModuleSubscriptionStatus.Requested);
        row.Source.Should().Be(ModuleSubscriptionSource.Request);
        row.RequestedByUserId.Should().Be(User);
        row.AcceptedMonthlyPrice.Should().Be(150000m);
        row.AcceptedCurrency.Should().Be("XOF", "the currency is normalised upper-case");
        row.ActivatedAt.Should().BeNull("nothing is billed before the platform authorises");
        row.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Approval_starts_the_billed_period()
    {
        var row = Requested();

        row.Approve(Admin, Now.AddHours(1)).Should().BeTrue();

        row.Status.Should().Be(ModuleSubscriptionStatus.Active);
        row.ActivatedAt.Should().Be(Now.AddHours(1));
        row.DecidedByAdminId.Should().Be(Admin);
    }

    [Fact]
    public void A_refusal_needs_a_reason_and_closes_the_row()
    {
        var row = Requested();

        row.Reject(Admin, "   ", Now).Should().BeFalse();
        row.Status.Should().Be(ModuleSubscriptionStatus.Requested);

        row.Reject(Admin, " Contrat non signé ", Now).Should().BeTrue();
        row.Status.Should().Be(ModuleSubscriptionStatus.Rejected);
        row.DecisionReason.Should().Be("Contrat non signé");
        row.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Only_a_pending_request_can_be_cancelled()
    {
        var row = Requested();
        row.Cancel(User, Now).Should().BeTrue();
        row.Status.Should().Be(ModuleSubscriptionStatus.Cancelled);

        var active = Requested();
        active.Approve(Admin, Now);
        active.Cancel(User, Now).Should().BeFalse("an active module is deactivated, not cancelled");
    }

    [Fact]
    public void Deactivation_ends_the_billed_period_and_only_applies_to_an_active_module()
    {
        var pending = Requested();
        pending.Deactivate(User, "admin@caisse.ci", Now).Should().BeFalse();

        var active = Requested();
        active.Approve(Admin, Now);
        active.Deactivate(User, "admin@caisse.ci", Now.AddDays(30)).Should().BeTrue();

        active.Status.Should().Be(ModuleSubscriptionStatus.Deactivated);
        active.DeactivatedAt.Should().Be(Now.AddDays(30));
        active.DeactivatedByEmail.Should().Be("admin@caisse.ci");
        active.ActivatedAt.Should().Be(Now, "the start of the period is kept for billing");
    }

    [Fact]
    public void A_closed_row_never_reopens()
    {
        var row = Requested();
        row.Approve(Admin, Now);
        row.Deactivate(User, "a@b.c", Now);

        row.Approve(Admin, Now).Should().BeFalse();
        row.Reject(Admin, "x", Now).Should().BeFalse();
        row.Cancel(User, Now).Should().BeFalse();
        row.Deactivate(User, "a@b.c", Now).Should().BeFalse();
        row.Status.Should().Be(ModuleSubscriptionStatus.Deactivated);
    }

    [Fact]
    public void A_direct_activation_is_active_at_once_and_cannot_pretend_to_be_a_request()
    {
        var row = TenantModuleSubscription.ActivateDirect(Tenant, TenantModules.BankingMicrofinance, null,
            ModuleSubscriptionSource.Migration, Now);

        row.Status.Should().Be(ModuleSubscriptionStatus.Active);
        row.ActivatedAt.Should().Be(Now);
        row.DecidedByAdminId.Should().BeNull();

        var act = () => TenantModuleSubscription.ActivateDirect(Tenant, TenantModules.Insurance, Admin,
            ModuleSubscriptionSource.Request, Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_definition_matches_only_the_exact_current_price()
    {
        var definition = ModuleDefinition.Create(TenantModules.Insurance, "Assurance", "", Now);
        definition.Update("Assurance", "", 150000m, "xof", isAvailable: true, Now);

        definition.Currency.Should().Be("XOF");
        definition.MatchesPrice(150000m, "XOF").Should().BeTrue();
        definition.MatchesPrice(150000m, " xof ").Should().BeTrue();
        definition.MatchesPrice(149999.99m, "XOF").Should().BeFalse();
        definition.MatchesPrice(150000m, "EUR").Should().BeFalse();
    }
}
