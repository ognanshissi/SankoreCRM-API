namespace Sankore.Modules.Notifications.Tests.Features.EmailTemplates;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Features.EmailTemplates.ForkEmailTemplate;
using Sankore.Modules.Notifications.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class ForkEmailTemplateHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly ICurrentUser _currentUser;
    private readonly SharedTestNotificationsDbContextFactory _factory;

    public ForkEmailTemplateHandlerTests()
    {
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(_tenantId);
        _factory = new SharedTestNotificationsDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Fork_succeeds_for_system_template()
    {
        await using var db = _factory.CreateContext();
        var system = EmailTemplate.Create(null, "user.activation", "fr", 1, "Sub", "<p>body</p>", isSystem: true);
        db.EmailTemplates.Add(system);
        await db.SaveChangesAsync();

        var handler = new ForkEmailTemplateHandler(db, _currentUser);
        var result = await handler.Handle(new ForkEmailTemplateCommand(system.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var fork = verify.EmailTemplates.Single(t => t.Id == result.Value);
        fork.TenantId.Should().Be(_tenantId);
        fork.TemplateKey.Should().Be("user.activation");
        fork.Locale.Should().Be("fr");
        fork.Version.Should().Be(1);
        fork.IsSystem.Should().BeFalse();
        fork.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Fork_fails_if_not_system_template()
    {
        await using var db = _factory.CreateContext();
        var tenant = EmailTemplate.Create(_tenantId, "custom", "fr", 1, "Sub", "<p>body</p>");
        db.EmailTemplates.Add(tenant);
        await db.SaveChangesAsync();

        var handler = new ForkEmailTemplateHandler(db, _currentUser);
        var result = await handler.Handle(new ForkEmailTemplateCommand(tenant.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NOT_A_SYSTEM_TEMPLATE");
    }

    [Fact]
    public async Task Fork_fails_if_tenant_override_already_exists()
    {
        await using var db = _factory.CreateContext();
        var system = EmailTemplate.Create(null, "user.activation", "fr", 1, "Sub", "<p>body</p>", isSystem: true);
        var existing = EmailTemplate.Create(_tenantId, "user.activation", "fr", 1, "Custom", "<p>custom</p>");
        db.EmailTemplates.AddRange(system, existing);
        await db.SaveChangesAsync();

        var handler = new ForkEmailTemplateHandler(db, _currentUser);
        var result = await handler.Handle(new ForkEmailTemplateCommand(system.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("TENANT_OVERRIDE_EXISTS");
    }

    [Fact]
    public async Task Fork_fails_when_template_not_found()
    {
        await using var db = _factory.CreateContext();
        var handler = new ForkEmailTemplateHandler(db, _currentUser);

        var result = await handler.Handle(new ForkEmailTemplateCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("NOT_FOUND");
    }
}
