namespace Sankore.Modules.Leads.Tests.Features.WorkflowConditions;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;
using Xunit;
using LeadRecordGate = Sankore.Modules.Leads.Infrastructure.LeadRecordGate;
using ProjectRecordGate = Sankore.Modules.Leads.Infrastructure.ProjectRecordGate;

/// <summary>
/// What a workflow « Formulaire » / « Formulaire de qualification » / « Vérification KYC » step asks
/// of a lead or a project: empty fields by their LABEL (native or custom), questionnaires still to
/// complete, the client it became. Presence only — the workflow never sees a value.
/// </summary>
public sealed class LeadRecordGateTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();

    public LeadRecordGateTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _administration.GetEntityFormAsync(_tenantId, FormEntity.Lead, Arg.Any<CancellationToken>())
            .Returns(new EntityFormDto(FormEntity.Lead, 1, [],
            [
                new FormField("email", FormFieldType.Text, "E-mail", 1, IsNative: true),
                new FormField("phoneNumber", FormFieldType.Text, "Téléphone", 2, IsNative: true),
                new FormField("revenu", FormFieldType.Text, "Revenu mensuel", 3),
                new FormField("source", FormFieldType.Text, "Source", 4, IsNative: true),
                new FormField("ancien", FormFieldType.Text, "Ancien champ", 5, IsArchived: true),
            ]));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Empty_fields_are_named_by_their_label_and_unchecked_ones_are_not_offered()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        lead.SetCustomFields("""{"revenu": "250000"}""");
        await using (var db = _factory.CreateContext())
        {
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
        }
        var gate = new LeadRecordGate(_factory.CreateContext(), _administration);

        (await gate.FieldsAsync(_tenantId, default)).Select(f => f.Key)
            .Should().Equal("email", "phoneNumber", "revenu");
        (await gate.MissingFieldsAsync(_tenantId, lead.Id, ["email", "phoneNumber", "revenu"], default))
            .Should().Equal("E-mail");
    }

    [Fact]
    public void Every_native_lead_field_a_person_fills_can_be_required_by_a_step()
    {
        // Profession, situation and address joined the lead's form on 2026-11-02 without joining the
        // gate: a « Formulaire » step could not offer them. A new native field now fails here until
        // it is given a presence check — or listed below as set by the system, not by a person.
        string[] setBySystem = ["agencyId", "channel", "leadSourceConfigId", "ownerId", "prospectType", "source"];

        NativeFormCatalog.For(FormEntity.Lead).Fields.Select(f => f.Key).Except(setBySystem)
            .Should().BeSubsetOf(Sankore.Modules.Leads.Infrastructure.LeadFormPresence.NativeKeys);
    }

    [Fact]
    public async Task A_project_counts_its_questionnaires_and_its_leads_own()
    {
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
        var project = Opportunity.CreateFromLead(_tenantId, lead.Id, "Crédit", "PRET", null, TimeProvider.System);
        var other = Opportunity.CreateFromLead(_tenantId, lead.Id, "Épargne", "EPARGNE", null, TimeProvider.System);
        await using (var db = _factory.CreateContext())
        {
            db.Leads.Add(lead);
            db.Opportunities.AddRange(project, other);
            db.QualificationResponses.AddRange(
                Expected(lead.Id, project.Id),
                Expected(lead.Id, null),
                Expected(lead.Id, other.Id));
            await db.SaveChangesAsync();
        }

        (await new ProjectRecordGate(_factory.CreateContext(), _administration)
            .PendingQuestionnairesAsync(_tenantId, project.Id, default)).Should().Be(2);
        (await new LeadRecordGate(_factory.CreateContext(), _administration)
            .PendingQuestionnairesAsync(_tenantId, lead.Id, default)).Should().Be(3);
    }

    [Fact]
    public async Task Only_a_credit_file_holds_its_signers_apart()
    {
        var agent = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var agency = Guid.NewGuid();
        var lead = Lead.Capture(_tenantId, "Awa Koné", "+2250700000000", LeadSource.Agency, "PRET", "fr", new(14.6928, -17.4467), null,
            TimeProvider.System, agencyId: agency);
        lead.SetOwner(agent).IsSuccess.Should().BeTrue();
        var owned = Opportunity.CreateFromLead(_tenantId, lead.Id, "Crédit", "", owner, TimeProvider.System);
        var byLead = Opportunity.CreateFromLead(_tenantId, lead.Id, "Crédit bis", "", null, TimeProvider.System);
        var forClient = Opportunity.CreateForCustomer(_tenantId, clientId, "Crédit client", "", owner, TimeProvider.System);
        var template = ProjectTemplate.Create(_tenantId, CreditFileTemplate.Name, null, null,
            CreditFileTemplate.Steps("analyse"), DateTimeOffset.UtcNow, CreditFileTemplate.Forecast, CreditFileTemplate.Fields);
        template.IsSuccess.Should().BeTrue(template.Error);
        await using (var db = _factory.CreateContext())
        {
            db.Leads.Add(lead);
            db.Opportunities.AddRange(owned, byLead, forClient);
            db.ProjectTemplates.Add(template.Value!);
            await db.SaveChangesAsync();
        }

        var rules = await GateWithTemplates().SigningRulesAsync(
            _tenantId, [owned.Id, byLead.Id, forClient.Id, Guid.NewGuid()], default);

        rules.Should().HaveCount(3);
        rules[owned.Id].Should().Be(new RecordSigningRule(owner, agency, null));
        rules[byLead.Id].Should().Be(new RecordSigningRule(agent, agency, null));
        rules[forClient.Id].Should().Be(new RecordSigningRule(owner, null, clientId));
    }

    [Fact]
    public async Task A_loan_without_a_duration_is_not_a_credit_file_and_keeps_the_usual_rule()
    {
        var project = Opportunity.CreateForCustomer(_tenantId, Guid.NewGuid(), "Crédit", "", Guid.NewGuid(), TimeProvider.System);
        var template = ProjectTemplate.Create(_tenantId, "Ancien prêt", null, null,
            CreditFileTemplate.Steps("analyse").Select(s => s with { RequiredFieldKeys = null }).ToList(),
            DateTimeOffset.UtcNow, CreditFileTemplate.Forecast);
        template.IsSuccess.Should().BeTrue(template.Error);
        await using (var db = _factory.CreateContext())
        {
            db.Opportunities.Add(project);
            db.ProjectTemplates.Add(template.Value!);
            await db.SaveChangesAsync();
        }

        (await GateWithTemplates().SigningRulesAsync(_tenantId, [project.Id], default)).Should().BeEmpty();
    }

    private ProjectRecordGate GateWithTemplates()
    {
        var db = _factory.CreateContext();
        return new ProjectRecordGate(db, _administration,
            new ProjectTemplateResolver(db, _administration, new FixedTenantContext(_tenantId)));
    }

    private QualificationResponse Expected(Guid leadId, Guid? projectId)
        => QualificationResponse.Expected(_tenantId, leadId, Guid.NewGuid(), 1, QualificationSubjectType.Lead, leadId,
            false, DateTimeOffset.UtcNow, projectId);
}
