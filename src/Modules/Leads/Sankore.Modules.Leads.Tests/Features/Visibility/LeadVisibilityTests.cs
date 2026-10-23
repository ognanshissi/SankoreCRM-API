namespace Sankore.Modules.Leads.Tests.Features.Visibility;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.GetLead;
using Sankore.Modules.Leads.Features.ListLeads;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// An Agent or a CommercialAgent consults only the leads assigned to them (product owner,
/// 2026-10-23): the ones they hold, and while nobody holds a lead, the one they collected.
/// </summary>
public sealed class LeadVisibilityTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly LeadsDbContext _db;

    public LeadVisibilityTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _db = _factory.CreateContext();
    }

    public void Dispose()
    {
        _db.Dispose();
        _factory.Dispose();
    }

    private ICurrentUser SignedIn(string role, params string[] permissions)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_me);
        user.TenantId.Returns(_tenantId);
        user.IsAuthenticated.Returns(true);
        user.Roles.Returns([role]);
        user.Permissions.Returns(permissions);
        return user;
    }

    private Lead Seed(string name, Guid? owner = null, Guid? assignedTo = null, Guid? collectedBy = null)
    {
        var lead = Lead.Capture(
            tenantId: _tenantId, fullName: name, phoneNumber: $"+22507{Random.Shared.Next(10000000, 99999999)}",
            source: LeadSource.Agency, interestedProduct: "Crédit", preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0), preferredAgencyId: null, clock: TimeProvider.System,
            ownerId: owner, agentCollectedLeadId: collectedBy);

        if (assignedTo is { } agent)
        {
            var assignment = LeadAssignment.Create(
                tenantId: _tenantId, leadId: lead.Id, agentId: agent,
                strategy: DispatchingStrategy.RoundRobin, compatibilityScore: 50,
                slaDeadline: DateTimeOffset.UtcNow.AddHours(4), createdAt: DateTimeOffset.UtcNow);
            _db.LeadAssignments.Add(assignment);
            lead.AssignTo(assignment, null);
        }

        _db.Leads.Add(lead);
        return lead;
    }

    private async Task<IReadOnlyList<string>> ListAsync(ICurrentUser user)
    {
        await _db.SaveChangesAsync();
        var result = await new ListLeadsHandler(_db, visibility: new LeadVisibility(user))
            .Handle(new ListLeadsQuery(PageSize: 100), default);
        result.IsSuccess.Should().BeTrue();
        return [.. result.Value!.Items.Select(l => l.FullName).Order()];
    }

    [Fact]
    public async Task An_agent_lists_the_leads_they_hold_or_own()
    {
        Seed("Assigné à moi", assignedTo: _me);
        Seed("Possédé par moi", owner: _me);
        Seed("Chez un collègue", assignedTo: _colleague);
        Seed("Sans personne");

        (await ListAsync(SignedIn(Roles.CommercialAgent.Code)))
            .Should().Equal("Assigné à moi", "Possédé par moi");
    }

    [Fact]
    public async Task The_collector_sees_a_lead_only_while_nobody_holds_it()
    {
        Seed("Collecté, en attente", collectedBy: _me);
        Seed("Collecté, confié à un collègue", collectedBy: _me, assignedTo: _colleague);

        (await ListAsync(SignedIn(Roles.Agent.Code))).Should().Equal("Collecté, en attente");
    }

    [Fact]
    public async Task Lead_read_all_and_the_administrator_see_every_lead()
    {
        Seed("A", assignedTo: _me);
        Seed("B", assignedTo: _colleague);
        Seed("C");

        (await ListAsync(SignedIn(Roles.SalesManager.Code, Permissions.CanReadAllLeads.Code))).Should().HaveCount(3);
        (await ListAsync(SignedIn(Roles.Administrator.Code))).Should().HaveCount(3,
            "a token issued before the permission existed still carries the role");
    }

    [Fact]
    public async Task A_read_by_id_of_a_colleagues_lead_is_not_found()
    {
        var theirs = Seed("Chez un collègue", assignedTo: _colleague);
        var mine = Seed("À moi", owner: _me);
        await _db.SaveChangesAsync();

        var user = SignedIn(Roles.Agent.Code);
        var services = new ServiceCollection()
            .AddSingleton(user)
            .AddSingleton(_db)
            .AddSingleton(new LeadVisibility(user))
            .BuildServiceProvider();
        var behavior = new LeadVisibilityBehavior<GetLeadQuery, Result<string>>(services);

        var refused = await behavior.Handle(
            new GetLeadQuery(theirs.Id), () => Task.FromResult(Result.Ok("reached")), default);
        var allowed = await behavior.Handle(
            new GetLeadQuery(mine.Id), () => Task.FromResult(Result.Ok("reached")), default);

        refused.Error.Should().Be("LEAD_NOT_FOUND", "never a 403: that would confirm the lead exists");
        allowed.Value.Should().Be("reached");
    }

    [Fact]
    public async Task A_background_identity_is_never_restricted()
    {
        Seed("B", assignedTo: _colleague);

        var system = new Sankore.Shared.Infrastructure.BackgroundJobs.SystemCurrentUser(_me, _tenantId, "SYSTEM");

        (await ListAsync(system)).Should().HaveCount(1);
    }
}
