namespace Sankore.Modules.Notifications.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class EmailTemplateForkTests
{
    private static EmailTemplate BuildSystem() =>
        EmailTemplate.Create(null, "user.activation", "fr", 1, "Activez", "<p>Bonjour</p>", isSystem: true);

    private static EmailTemplate BuildTenant(Guid tenantId) =>
        EmailTemplate.Create(tenantId, "custom", "fr", 1, "Sub", "<p>Body</p>");

    [Fact]
    public void Fork_creates_tenant_copy_with_IsSystem_false()
    {
        var system = BuildSystem();
        var tenantId = Guid.NewGuid();

        var fork = system.Fork(tenantId);

        fork.Id.Should().NotBe(system.Id);
        fork.TenantId.Should().Be(tenantId);
        fork.TemplateKey.Should().Be(system.TemplateKey);
        fork.Locale.Should().Be(system.Locale);
        fork.Version.Should().Be(1);
        fork.Subject.Should().Be(system.Subject);
        fork.HtmlBody.Should().Be(system.HtmlBody);
        fork.TextBody.Should().Be(system.TextBody);
        fork.IsActive.Should().BeTrue();
        fork.IsSystem.Should().BeFalse();
    }

    [Fact]
    public void Fork_throws_when_not_a_system_template()
    {
        var tenant = BuildTenant(Guid.NewGuid());

        var act = () => tenant.Fork(Guid.NewGuid());

        act.Should().Throw<DomainException>()
            .WithMessage("*NOT_A_SYSTEM_TEMPLATE*");
    }

    [Fact]
    public void EnsureMutable_throws_on_system_template()
    {
        var system = BuildSystem();

        var act = () => system.EnsureMutable();

        act.Should().Throw<DomainException>()
            .WithMessage("*SYSTEM_TEMPLATE_READONLY*");
    }

    [Fact]
    public void EnsureMutable_succeeds_on_non_system_template()
    {
        var tenant = BuildTenant(Guid.NewGuid());

        var act = () => tenant.EnsureMutable();

        act.Should().NotThrow();
    }

    [Fact]
    public void Create_with_isSystem_true_sets_flag()
    {
        var t = BuildSystem();
        t.IsSystem.Should().BeTrue();
    }

    [Fact]
    public void Create_defaults_IsSystem_to_false()
    {
        var t = EmailTemplate.Create(Guid.NewGuid(), "key", "en", 1, "Sub", "<p>b</p>");
        t.IsSystem.Should().BeFalse();
    }
}
