namespace Sankore.Modules.Notifications.Tests.Features.EmailTemplates;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.EmailTemplates.ListEmailTemplates;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class ListEmailTemplatesHandlerTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ICurrentUser _currentUser;

    public ListEmailTemplatesHandlerTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
    }

    [Fact]
    public async Task Returns_tenant_and_platform_templates()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        db.EmailTemplates.Add(EmailTemplate.Create(_tenantId, "welcome", "fr", 1, "Tenant tpl", "<p></p>"));
        db.EmailTemplates.Add(EmailTemplate.Create(null, "reset-password", "fr", 1, "Platform tpl", "<p></p>"));
        db.EmailTemplates.Add(EmailTemplate.Create(Guid.NewGuid(), "other-tenant", "fr", 1, "Other", "<p></p>"));
        await db.SaveChangesAsync();

        var handler = new ListEmailTemplatesHandler(db, _currentUser);
        var result = await handler.Handle(new ListEmailTemplatesQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        result.Value.Select(t => t.TemplateKey).Should().Contain(["welcome", "reset-password"]);
    }

    [Fact]
    public async Task Filters_by_template_key()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        db.EmailTemplates.Add(EmailTemplate.Create(_tenantId, "welcome", "fr", 1, "A", "<p></p>"));
        db.EmailTemplates.Add(EmailTemplate.Create(_tenantId, "invoice", "fr", 1, "B", "<p></p>"));
        await db.SaveChangesAsync();

        var handler = new ListEmailTemplatesHandler(db, _currentUser);
        var result = await handler.Handle(new ListEmailTemplatesQuery(TemplateKey: "welcome"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(t => t.TemplateKey == "welcome");
    }

    [Fact]
    public async Task Filters_by_is_active()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var active = EmailTemplate.Create(_tenantId, "welcome", "fr", 1, "Active", "<p></p>");
        var inactive = EmailTemplate.Create(_tenantId, "welcome", "fr", 2, "Inactive", "<p></p>");
        inactive.Deactivate();
        db.EmailTemplates.AddRange(active, inactive);
        await db.SaveChangesAsync();

        var handler = new ListEmailTemplatesHandler(db, _currentUser);
        var result = await handler.Handle(new ListEmailTemplatesQuery(IsActive: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.IsActive.Should().BeTrue();
    }
}
