namespace Sankore.Modules.Leads.Tests.LeadAggregate;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// Projets, lot 4 : un lead s'arrête à « besoin identifié ». Qualification, proposition, dossier,
/// pièces et approbation sont les étapes d'un projet ; le lead n'y est plus déplacé.
/// </summary>
public sealed class LeadPipelineStopsAtNeedTests
{
    [Theory]
    [InlineData(PipelineStage.Qualified)]
    [InlineData(PipelineStage.ProductProposed)]
    [InlineData(PipelineStage.ApplicationStarted)]
    [InlineData(PipelineStage.DocumentCollection)]
    [InlineData(PipelineStage.ApplicationCompleted)]
    [InlineData(PipelineStage.ApprovalPending)]
    public void A_lead_is_never_moved_to_an_application_stage(PipelineStage stage)
    {
        var lead = LeadTestBuilder.Create().Build();
        var before = lead.PipelineStage;

        var result = lead.AdvancePipelineStage(stage);

        result.Error.Should().Be(LeadPipeline.StageIsAProjectStep);
        lead.PipelineStage.Should().Be(before);
    }

    [Theory]
    [InlineData(PipelineStage.ContactAttempted)]
    [InlineData(PipelineStage.ContactEstablished)]
    [InlineData(PipelineStage.NeedIdentified)]
    [InlineData(PipelineStage.Lost)]
    public void The_contact_stages_and_lost_stay_open(PipelineStage stage)
        => LeadTestBuilder.Create().Build().AdvancePipelineStage(stage).IsSuccess.Should().BeTrue();

    [Fact]
    public void Converted_is_never_reached_by_moving_the_lead()
    {
        // A lead converts when one of its projects is approved (product owner, 2026-10-24); the
        // old Kanban drop left a « converted » lead with no client.
        var lead = LeadTestBuilder.Create().Build();

        var result = lead.AdvancePipelineStage(PipelineStage.Converted);

        result.Error.Should().Be(LeadPipeline.ConvertedByProjectApproval);
        lead.Status.Should().NotBe(LeadStatus.Converted);
    }

    [Fact]
    public void A_report_shows_the_contact_stages_and_the_two_outcomes()
        => LeadPipeline.ReportStages.Should().Equal(
            PipelineStage.New, PipelineStage.ContactAttempted, PipelineStage.ContactEstablished,
            PipelineStage.NeedIdentified, PipelineStage.Converted, PipelineStage.Lost);
}
