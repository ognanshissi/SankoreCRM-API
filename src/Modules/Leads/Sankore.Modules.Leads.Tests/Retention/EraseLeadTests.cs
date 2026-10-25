namespace Sankore.Modules.Leads.Tests.Retention;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Retention;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// Erasing one lead on a person's request (data-purge lot 2): the retention job's cascade, plus what
/// agents wrote about the person — which neither path erased before.
/// </summary>
public sealed class EraseLeadTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public EraseLeadTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_lead_and_what_agents_wrote_about_the_person_are_erased_and_announced()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        var activity = LeadActivity.Create(_tenant, lead.Id, ActivityType.Call, "Appel avec Mme Konan", Guid.NewGuid(),
            notes: "Divorcée, deux enfants, revenus 450 000 FCFA.");
        await using (var seed = _factory.CreateContext())
        {
            seed.Leads.Add(lead);
            seed.LeadActivities.Add(activity);
            await seed.SaveChangesAsync();
        }

        await using (var db = _factory.CreateContext())
        {
            var erased = await new EraseLeadHandler(db, TimeProvider.System, _publisher)
                .Handle(new EraseLeadCommand(_tenant, lead.Id), default);
            erased.Should().BeTrue();
        }

        await using var reread = _factory.CreateContext();
        (await reread.Leads.SingleAsync(l => l.Id == lead.Id)).IsAnonymized.Should().BeTrue();
        var storedActivity = await reread.LeadActivities.SingleAsync(a => a.Id == activity.Id);
        storedActivity.Notes.Should().BeNull();
        storedActivity.Subject.Should().Be("[anonymisé]");
        await _publisher.Received(1).PublishAsync(
            Arg.Is<LeadErasedEvent>(e => e.LeadId == lead.Id && e.TenantId == _tenant), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Erasing_twice_announces_nothing_the_second_time()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        await using (var seed = _factory.CreateContext())
        {
            seed.Leads.Add(lead);
            await seed.SaveChangesAsync();
        }

        await using (var first = _factory.CreateContext())
            (await new EraseLeadHandler(first, TimeProvider.System, _publisher).Handle(new EraseLeadCommand(_tenant, lead.Id), default))
                .Should().BeTrue();
        _publisher.ClearReceivedCalls();

        await using var second = _factory.CreateContext();
        (await new EraseLeadHandler(second, TimeProvider.System, _publisher).Handle(new EraseLeadCommand(_tenant, lead.Id), default))
            .Should().BeFalse();
        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync<LeadErasedEvent>(default!, default);
    }
}
