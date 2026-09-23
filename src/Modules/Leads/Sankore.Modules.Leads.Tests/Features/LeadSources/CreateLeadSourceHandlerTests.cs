namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;
using Sankore.Modules.Leads.Features.LeadSources.Events;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

public sealed class CreateLeadSourceHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IEventPublisher _publisher;

    public CreateLeadSourceHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _publisher = Substitute.For<IEventPublisher>();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Create_sets_status_to_Draft_and_publishes_event()
    {
        await using var db = _factory.CreateContext();
        var handler = new CreateLeadSourceHandler(db, _publisher);

        var result = await handler.Handle(new CreateLeadSourceCommand(
            TenantId: _tenantId,
            Code: "WEB",
            Label: "Web Form",
            ChannelType: LeadChannelType.WebForm,
            DisplayOrder: 0), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var source = verify.LeadSourceConfigs.Single(s => s.Id == result.Value);
        source.Status.Should().Be(LeadSourceStatus.Draft);
        source.PublicKey.Should().NotBeNullOrEmpty(); // auto-generated for EmbeddedScript

        await _publisher.Received(1).PublishAsync(
            Arg.Is<LeadSourceChangedEvent>(e =>
                e.ChangeType == "Created" && e.Code == "WEB"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_rejects_duplicate_code()
    {
        await using var db = _factory.CreateContext();
        db.LeadSourceConfigs.Add(LeadSourceConfig.Create(
            _tenantId, "WEB", "Existing", LeadChannelType.WebForm, 0));
        await db.SaveChangesAsync();

        var handler = new CreateLeadSourceHandler(db, _publisher);
        var result = await handler.Handle(new CreateLeadSourceCommand(
            TenantId: _tenantId,
            Code: "WEB",
            Label: "Another",
            ChannelType: LeadChannelType.WebForm,
            DisplayOrder: 1), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_SOURCE_CODE_ALREADY_EXISTS");
    }
}
