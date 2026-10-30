namespace Sankore.Modules.Leads.Tests.Features.WorkflowConditions;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;
using LeadContextProvider = Sankore.Modules.Leads.Infrastructure.LeadContextProvider;
using ProjectContextProvider = Sankore.Modules.Leads.Infrastructure.ProjectContextProvider;

/// <summary>
/// Every field the condition editor offers is filled by the provider: a key declared and never
/// filled would make a « Condition » step compare an empty value — silently, always « Non ».
/// </summary>
public sealed class ContextProviderFieldsTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public ContextProviderFieldsTests() => _factory = new TestDbContextFactory(_tenantId);
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_lead_and_project_providers_fill_every_field_they_declare()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        var project = Opportunity.CreateFromLead(_tenantId, lead.Id, "Crédit", "PRET", null, TimeProvider.System);
        await using (var db = _factory.CreateContext())
        {
            db.Leads.Add(lead);
            db.Opportunities.Add(project);
            await db.SaveChangesAsync();
        }

        var leadProvider = new LeadContextProvider(_factory.CreateContext());
        var leadFacts = await leadProvider.BuildAsync(lead.Id, _tenantId);
        leadFacts.Keys.Should().BeEquivalentTo(leadProvider.Fields.Select(f => f.Key));
        leadFacts["score"].Should().Be(75d);
        leadFacts["status"].Should().Be("Qualified");

        var projectProvider = new ProjectContextProvider(_factory.CreateContext());
        var projectFacts = await projectProvider.BuildAsync(project.Id, _tenantId);
        projectFacts.Keys.Should().BeEquivalentTo(projectProvider.Fields.Select(f => f.Key));
        projectFacts["product"].Should().Be("PRET");
        projectFacts["lead.score"].Should().Be(75d);
    }
}
