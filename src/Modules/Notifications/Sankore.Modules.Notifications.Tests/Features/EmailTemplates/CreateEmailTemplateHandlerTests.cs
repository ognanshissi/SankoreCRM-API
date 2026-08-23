namespace Sankore.Modules.Notifications.Tests.Features.EmailTemplates;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.EmailTemplates.CreateEmailTemplate;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class CreateEmailTemplateHandlerTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ICurrentUser _currentUser;

    public CreateEmailTemplateHandlerTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
    }

    [Fact]
    public async Task Creates_template_with_version_1_when_none_exist()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var handler = new CreateEmailTemplateHandler(db, _currentUser);
        var cmd = new CreateEmailTemplateCommand("welcome", "fr", "Bienvenue", "<p>Bonjour</p>", null);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);

        var created = db.EmailTemplates.Single(t => t.Id == result.Value);
        created.Version.Should().Be(1);
    }

    [Fact]
    public async Task Auto_increments_version_when_existing_versions_present()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        db.EmailTemplates.Add(EmailTemplate.Create(_tenantId, "welcome", "fr", 1, "v1", "<p>v1</p>"));
        await db.SaveChangesAsync();

        var handler = new CreateEmailTemplateHandler(db, _currentUser);
        var cmd = new CreateEmailTemplateCommand("welcome", "fr", "v2 Subject", "<p>v2</p>", null);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.EmailTemplates.Single(t => t.Id == result.Value).Version.Should().Be(2);
    }

    [Fact]
    public async Task IsGlobal_true_sets_TenantId_to_null()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var handler = new CreateEmailTemplateHandler(db, _currentUser);
        var cmd = new CreateEmailTemplateCommand("platform-tpl", "en", "Subject", "<p>body</p>", null, IsGlobal: true);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        db.EmailTemplates.Single(t => t.Id == result.Value).TenantId.Should().BeNull();
    }

    [Fact]
    public async Task IsGlobal_false_sets_TenantId_from_current_user()
    {
        var db = TestNotificationsDbContextFactory.Create(_tenantId);
        var handler = new CreateEmailTemplateHandler(db, _currentUser);
        var cmd = new CreateEmailTemplateCommand("welcome", "fr", "Sub", "<p>b</p>", null, IsGlobal: false);

        var result = await handler.Handle(cmd, CancellationToken.None);

        db.EmailTemplates.Single(t => t.Id == result.Value).TenantId.Should().Be(_tenantId);
    }
}
