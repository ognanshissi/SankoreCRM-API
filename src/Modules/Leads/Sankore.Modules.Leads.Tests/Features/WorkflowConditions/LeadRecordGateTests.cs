namespace Sankore.Modules.Leads.Tests.Features.WorkflowConditions;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Tests.TestSupport;
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

    private QualificationResponse Expected(Guid leadId, Guid? projectId)
        => QualificationResponse.Expected(_tenantId, leadId, Guid.NewGuid(), 1, QualificationSubjectType.Lead, leadId,
            false, DateTimeOffset.UtcNow, projectId);
}
