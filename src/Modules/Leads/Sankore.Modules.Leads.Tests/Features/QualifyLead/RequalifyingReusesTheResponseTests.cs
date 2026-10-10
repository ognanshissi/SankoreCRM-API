namespace Sankore.Modules.Leads.Tests.Features.QualifyLead;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// A response is identified by (lead, template, subject type, subject id) and
/// <c>ux_qualification_responses_subject</c> enforces it in PostgreSQL. Qualifying the same lead
/// twice against the same template must therefore RE-ANSWER the row, not add a second one.
///
/// <para>
/// This is not something the suite's provider can fail on: EF InMemory honours no unique index, so
/// a blind <c>Add</c> here passes in tests and raises 23505 in production. The assertions below
/// count rows instead, which holds on both.
/// </para>
/// </summary>
public sealed class RequalifyingReusesTheResponseTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public RequalifyingReusesTheResponseTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private QualifyLeadHandler Handler(LeadsDbContext db) =>
        new(db,
            new LeadScoreCalculator(db),
            TimeProvider.System,
            Options.Create(new LeadModuleSettings()));

    private Lead NewLead() => Lead.Capture(
        tenantId: _tenant,
        fullName: "Aya Kouassi",
        phoneNumber: "+2250700000001",
        source: LeadSource.Agency,
        interestedProduct: "Crédit commerçant",
        preferredLanguage: "fr",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System,
        firstName: "Aya",
        lastName: "Kouassi",
        ownerId: Guid.NewGuid());

    private static QualificationTemplate PublishedTemplate(Guid tenant, out Guid questionId)
    {
        var now = DateTimeOffset.UtcNow;
        var template = QualificationTemplate.Create(
            tenant, "Besoin de financement", now, productCategory: ProductCategory.Loan);

        template.AddQuestion("Avez-vous un revenu régulier ?", QuestionType.YesNo, weight: 10,
            isRequired: true).IsSuccess.Should().BeTrue();

        questionId = template.Questions.Single().Id;
        template.Publish(now).IsSuccess.Should().BeTrue();
        return template;
    }

    /// <summary>
    /// Seeds the lead and template, then qualifies it <paramref name="times"/> times, each through
    /// its own context so nothing is carried in a change tracker between attempts.
    /// </summary>
    private async Task<(Guid LeadId, Guid TemplateId, Guid QuestionId, List<Guid?> ResponseIds)>
        QualifyRepeatedly(params string[] answers)
    {
        var lead = NewLead();
        var template = PublishedTemplate(_tenant, out var questionId);

        await using (var seed = _factory.CreateContext())
        {
            seed.Leads.Add(lead);
            seed.QualificationTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        var responseIds = new List<Guid?>();

        foreach (var answer in answers)
        {
            await using var db = _factory.CreateContext();
            var result = await Handler(db).Handle(
                new QualifyLeadCommand(
                    LeadId: lead.Id,
                    QualifiedBy: _agent,
                    TemplateId: template.Id,
                    Answers: [new QualificationAnswerInput(questionId, answer)]),
                CancellationToken.None);

            result.IsSuccess.Should().BeTrue(result.Error);
            responseIds.Add(result.Value!.QualificationResponseId);
        }

        return (lead.Id, template.Id, questionId, responseIds);
    }

    [Fact]
    public async Task A_second_qualification_reuses_the_same_response_row()
    {
        var (leadId, templateId, _, responseIds) = await QualifyRepeatedly("Oui", "Non");

        await using var verify = _factory.CreateContext();
        var rows = await verify.QualificationResponses
            .Where(r => r.LeadId == leadId && r.TemplateId == templateId)
            .ToListAsync();

        // One row, not two: the unique index would otherwise refuse the insert in PostgreSQL.
        rows.Should().HaveCount(1);
        rows[0].SubjectType.Should().Be(QualificationSubjectType.Lead);
        rows[0].SubjectId.Should().Be(leadId, "a lead-scoped response carries the lead's own id");

        // The handler reports the same id both times, so ScoreHistory keeps pointing at a row
        // that exists.
        responseIds.Should().OnlyContain(id => id == responseIds[0]);
        responseIds[0].Should().Be(rows[0].Id);
    }

    [Fact]
    public async Task Re_qualifying_overwrites_the_answers_with_the_latest_ones()
    {
        var (leadId, _, questionId, _) = await QualifyRepeatedly("Oui", "Non");

        await using var verify = _factory.CreateContext();
        var row = await verify.QualificationResponses.SingleAsync(r => r.LeadId == leadId);

        row.GetAnswers().Should().ContainSingle()
           .Which.Should().Be((questionId, "Non"));
        row.Status.Should().Be(QualificationResponseStatus.Completed);
    }

    [Fact]
    public async Task Every_qualification_still_leaves_its_own_score_history_entry()
    {
        var (leadId, _, _, _) = await QualifyRepeatedly("Oui", "Non", "Oui");

        await using var verify = _factory.CreateContext();
        var history = await verify.ScoreHistories.Where(s => s.LeadId == leadId).ToListAsync();

        // Collapsing the responses costs no history: this is the table the lead's timeline reads
        // for the succession of scores, and it is why the migration can drop superseded responses.
        history.Should().HaveCount(3);
        history.Should().OnlyContain(s => s.QualificationResponseId != null);
    }
}
