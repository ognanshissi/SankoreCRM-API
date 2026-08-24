namespace Sankore.Modules.Notifications.Tests.Features.EmailTemplates;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.EmailTemplates.ActivateEmailTemplate;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class ActivateEmailTemplateHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ICurrentUser _currentUser;
    private readonly SharedTestNotificationsDbContextFactory _factory;

    public ActivateEmailTemplateHandlerTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
        _factory = new SharedTestNotificationsDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async void Activates_target_and_deactivates_siblings()
    {
        await using var db = _factory.CreateContext();
        var v1 = EmailTemplate.Create(_tenantId, "welcome", "fr", 1, "v1", "<p>v1</p>");
        var v2 = EmailTemplate.Create(_tenantId, "welcome", "fr", 2, "v2", "<p>v2</p>");
        v2.Deactivate();
        db.EmailTemplates.AddRange(v1, v2);
        await db.SaveChangesAsync();

        var handler = new ActivateEmailTemplateHandler(db, _currentUser);
        var result = await handler.Handle(new ActivateEmailTemplateCommand(v2.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        verify.EmailTemplates.Single(t => t.Id == v1.Id).IsActive.Should().BeFalse();
        verify.EmailTemplates.Single(t => t.Id == v2.Id).IsActive.Should().BeTrue();
    }

    [Fact]
    public async void Returns_failure_when_template_not_found()
    {
        await using var db = _factory.CreateContext();
        var handler = new ActivateEmailTemplateHandler(db, _currentUser);

        var result = await handler.Handle(
            new ActivateEmailTemplateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("NOT_FOUND");
    }

    [Fact]
    public async void Platform_template_is_visible_and_activatable_by_any_tenant()
    {
        await using var db = _factory.CreateContext();
        var platform = EmailTemplate.Create(null, "welcome", "fr", 1, "Platform", "<p>global</p>");
        db.EmailTemplates.Add(platform);
        await db.SaveChangesAsync();

        var handler = new ActivateEmailTemplateHandler(db, _currentUser);
        var result = await handler.Handle(
            new ActivateEmailTemplateCommand(platform.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
