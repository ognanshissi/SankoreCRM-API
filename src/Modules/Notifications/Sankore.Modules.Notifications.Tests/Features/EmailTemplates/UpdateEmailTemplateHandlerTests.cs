namespace Sankore.Modules.Notifications.Tests.Features.EmailTemplates;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.EmailTemplates.UpdateEmailTemplate;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class UpdateEmailTemplateHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ICurrentUser _currentUser;
    private readonly SharedTestNotificationsDbContextFactory _factory;

    public UpdateEmailTemplateHandlerTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
        _factory = new SharedTestNotificationsDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async void Creates_new_version_and_deactivates_previous()
    {
        await using var db = _factory.CreateContext();
        var existing = EmailTemplate.Create(_tenantId, "welcome", "fr", 1, "v1 Subject", "<p>v1</p>");
        db.EmailTemplates.Add(existing);
        await db.SaveChangesAsync();

        var handler = new UpdateEmailTemplateHandler(db, _currentUser);
        var result = await handler.Handle(
            new UpdateEmailTemplateCommand(existing.Id, "v2 Subject", "<p>v2</p>", null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(existing.Id);

        await using var verify = _factory.CreateContext();
        verify.EmailTemplates.Single(t => t.Id == existing.Id).IsActive.Should().BeFalse();
        verify.EmailTemplates.Single(t => t.Id == result.Value).Version.Should().Be(2);
        verify.EmailTemplates.Single(t => t.Id == result.Value).Subject.Should().Be("v2 Subject");
    }

    [Fact]
    public async void Returns_failure_when_source_template_not_found()
    {
        await using var db = _factory.CreateContext();
        var handler = new UpdateEmailTemplateHandler(db, _currentUser);

        var result = await handler.Handle(
            new UpdateEmailTemplateCommand(Guid.NewGuid(), "Sub", "<p>body</p>", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("NOT_FOUND");
    }

    [Fact]
    public async void Cannot_update_another_tenants_template()
    {
        var otherTenantId = Guid.NewGuid();
        var otherFactory = new SharedTestNotificationsDbContextFactory(otherTenantId);
        await using var seedDb = otherFactory.CreateContext();
        var template = EmailTemplate.Create(otherTenantId, "welcome", "fr", 1, "Sub", "<p>b</p>");
        seedDb.EmailTemplates.Add(template);
        await seedDb.SaveChangesAsync();

        // Handler running as _tenantId — cannot see otherTenantId's template
        await using var myDb = _factory.CreateContext();
        var handler = new UpdateEmailTemplateHandler(myDb, _currentUser);

        var result = await handler.Handle(
            new UpdateEmailTemplateCommand(template.Id, "New", "<p>new</p>", null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}
