namespace Sankore.Modules.Notifications.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Notifications.Domain;
using Xunit;

public sealed class EmailTemplateTests
{
    private static EmailTemplate Build(Guid? tenantId = null) =>
        EmailTemplate.Create(tenantId, "welcome", "fr", 1, "Bienvenue", "<p>Bonjour</p>");

    [Fact]
    public void Create_sets_IsActive_true_and_correct_fields()
    {
        var tenantId = Guid.NewGuid();
        var t = Build(tenantId);

        t.IsActive.Should().BeTrue();
        t.TenantId.Should().Be(tenantId);
        t.TemplateKey.Should().Be("welcome");
        t.Locale.Should().Be("fr");
        t.Version.Should().Be(1);
        t.Subject.Should().Be("Bienvenue");
    }

    [Fact]
    public void Create_with_null_tenantId_produces_platform_template()
    {
        var t = Build(null);
        t.TenantId.Should().BeNull();
    }

    [Fact]
    public void Deactivate_sets_IsActive_to_false()
    {
        var t = Build();
        t.Deactivate();
        t.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_sets_IsActive_to_true()
    {
        var t = Build();
        t.Deactivate();
        t.Activate();
        t.IsActive.Should().BeTrue();
    }
}
