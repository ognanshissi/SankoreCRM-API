namespace Sankore.Modules.Leads.Tests.Features.LeadSources;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.LeadSources.Events;
using Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

public sealed class UpdateLeadSourceHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IEventPublisher _publisher;

    public UpdateLeadSourceHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _publisher = Substitute.For<IEventPublisher>();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Update_succeeds_and_publishes_changed_fields()
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "WEB", "Original", LeadChannelType.WebForm, 0);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();

        var handler = new UpdateLeadSourceHandler(db, _publisher);
        var result = await handler.Handle(new UpdateLeadSourceCommand(
            SourceId: source.Id,
            ExpectedVersion: source.Version,
            Label: "Updated",
            DisplayOrder: 5), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _publisher.Received(1).PublishAsync(
            Arg.Is<LeadSourceChangedEvent>(e =>
                e.ChangeType == "Updated"
                && e.ChangedFields.Contains("Label")
                && e.ChangedFields.Contains("DisplayOrder")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_persists_dispatching_defaults()
    {
        var agencyId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "WEB", "Label", LeadChannelType.WebForm, 0,
            defaultAgencyId: agencyId, defaultDispatchingRuleId: ruleId);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();

        var handler = new UpdateLeadSourceHandler(db, _publisher);
        var result = await handler.Handle(new UpdateLeadSourceCommand(
            SourceId: source.Id,
            ExpectedVersion: source.Version,
            Label: "Updated",
            DisplayOrder: 0,
            DefaultAgencyId: agencyId,
            DefaultDispatchingRuleId: ruleId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        source.DefaultAgencyId.Should().Be(agencyId);
        source.DefaultDispatchingRuleId.Should().Be(ruleId);
    }

    [Fact]
    public async Task Update_returns_409_on_version_mismatch()
    {
        await using var db = _factory.CreateContext();
        var source = LeadSourceConfig.Create(
            _tenantId, "WEB", "Label", LeadChannelType.WebForm, 0);
        db.LeadSourceConfigs.Add(source);
        await db.SaveChangesAsync();

        var handler = new UpdateLeadSourceHandler(db, _publisher);
        var result = await handler.Handle(new UpdateLeadSourceCommand(
            SourceId: source.Id,
            ExpectedVersion: 99999, // wrong version
            Label: "Updated",
            DisplayOrder: 0), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CONFLICT");
    }

    [Fact]
    public async Task Update_returns_not_found_for_missing_source()
    {
        await using var db = _factory.CreateContext();
        var handler = new UpdateLeadSourceHandler(db, _publisher);

        var result = await handler.Handle(new UpdateLeadSourceCommand(
            SourceId: Guid.NewGuid(),
            ExpectedVersion: 0,
            Label: "Label",
            DisplayOrder: 0), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_SOURCE_NOT_FOUND");
    }
}
