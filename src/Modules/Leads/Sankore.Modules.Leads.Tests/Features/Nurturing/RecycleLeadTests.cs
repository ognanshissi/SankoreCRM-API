namespace Sankore.Modules.Leads.Tests.Features.Nurturing;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ReactivateRecycledLeads;
using Sankore.Modules.Leads.Features.RecycleLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// « Recycler » used to send an empty body: the reason and the date the screen asks for, and the
/// resumption task it announces, did not exist.
/// </summary>
public sealed class RecycleLeadTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public RecycleLeadTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedAsync(DateOnly? recycledUntil = null, bool recycled = false)
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        lead.SetOwner(_owner);
        lead.Nurture();
        if (recycled) lead.Recycle(until: recycledUntil);
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead.Id;
    }

    private async Task<Sankore.Shared.Kernel.Result> RecycleAsync(Guid id, string? reason, DateOnly? on)
    {
        await using var db = _factory.CreateContext();
        return await new RecycleLeadHandler(db, TimeProvider.System).Handle(
            new RecycleLeadCommand(id, Reason: reason, ReactivateOn: on), CancellationToken.None);
    }

    private async Task RunJobAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<LeadsDbContext>(_ => _factory.CreateContext());
        services.AddSingleton(Substitute.For<ISender>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new Sankore.Modules.Leads.LeadModuleSettings()));
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await new ReactivateRecycledLeadsJob(provider.GetRequiredService<IServiceScopeFactory>()).ExecuteAsync(_tenant);
    }

    private async Task<Lead> LeadAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.SingleAsync(l => l.Id == id);
    }

    [Fact]
    public async Task Recycling_keeps_the_day_and_gives_the_holder_a_task_carrying_the_reason()
    {
        var id = await SeedAsync();
        var day = Today.AddDays(30);

        (await RecycleAsync(id, "Budget non disponible", day)).IsSuccess.Should().BeTrue();

        var lead = await LeadAsync(id);
        lead.Status.Should().Be(LeadStatus.Recycled);
        lead.RecycledUntil.Should().Be(day);

        await using var db = _factory.CreateContext();
        var task = await db.CrmTasks.SingleAsync(t => t.LeadId == id);
        task.AssignedAgentId.Should().Be(_owner);
        DateOnly.FromDateTime(task.DueAt.UtcDateTime).Should().Be(day);
        task.Description.Should().Contain("Budget non disponible");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task A_day_that_is_not_in_the_future_is_refused(int days)
    {
        var id = await SeedAsync();

        (await RecycleAsync(id, "Motif", Today.AddDays(days))).Error.Should().Be("REACTIVATION_DATE_NOT_IN_FUTURE");

        (await LeadAsync(id)).Status.Should().Be(LeadStatus.Nurturing);
    }

    [Fact]
    public async Task Without_a_day_there_is_no_task_as_for_a_bulk_recycle()
    {
        var id = await SeedAsync();

        (await RecycleAsync(id, null, null)).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        (await db.CrmTasks.AnyAsync(t => t.LeadId == id)).Should().BeFalse();
    }

    [Fact]
    public async Task The_job_brings_a_lead_back_on_its_day()
    {
        var id = await SeedAsync(Today, recycled: true);

        await RunJobAsync();

        var lead = await LeadAsync(id);
        lead.Status.Should().Be(LeadStatus.Open);
        lead.RecycledUntil.Should().BeNull();
    }

    [Fact]
    public async Task The_job_leaves_a_lead_whose_day_has_not_come()
    {
        var id = await SeedAsync(Today.AddDays(1), recycled: true);

        await RunJobAsync();

        (await LeadAsync(id)).Status.Should().Be(LeadStatus.Recycled);
    }
}
