namespace Sankore.Modules.Leads.Tests.Features.Questionnaires;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Covers the multi-person and health flags on the template (BNF-B-07).</summary>
public sealed class HealthQuestionnaireTemplateTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static QualificationTemplate Draft(ProductCategory? category = ProductCategory.HealthInsurance)
        => QualificationTemplate.Create(Tenant, "Questionnaire santé", Now, productCategory: category);

    // ── Defaults: existing templates behave exactly as before ─────────────

    [Fact]
    public void A_new_template_is_lead_scoped_and_scored()
    {
        var template = Draft();

        template.SubjectScope.Should().Be(QualificationSubjectScope.Lead);
        template.IsPerPerson.Should().BeFalse();
        template.IsHealthQuestionnaire.Should().BeFalse();
        template.ExcludeFromScoring.Should().BeFalse();
    }

    // ── The health / insurance pairing ────────────────────────────────────

    [Theory]
    [InlineData(ProductCategory.Insurance)]
    [InlineData(ProductCategory.HealthInsurance)]
    [InlineData(ProductCategory.ForecastInsurance)]
    public void A_health_questionnaire_is_allowed_on_any_insurance_category(ProductCategory category)
    {
        // THREE values, not one: the Kernel splits insurance three ways, and reading the
        // specification's "Insurance" as the single member of that name would refuse the very
        // product a health questionnaire is written for.
        Draft(category)
            .SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false)
            .IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(ProductCategory.Loan)]
    [InlineData(ProductCategory.Savings)]
    [InlineData(ProductCategory.Tontine)]
    [InlineData(ProductCategory.CurrentAccount)]
    public void A_health_questionnaire_is_refused_on_a_non_insurance_category(ProductCategory category)
    {
        Draft(category)
            .SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false)
            .Error.Should().Be("HEALTH_QUESTIONNAIRE_REQUIRES_INSURANCE_LINE");
    }

    [Fact]
    public void A_health_questionnaire_with_no_category_at_all_is_refused()
    {
        // "Generic health questions" would be collected from every lead of every product line,
        // which is the opposite of a purpose limitation.
        Draft(category: null)
            .SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false)
            .Error.Should().Be("HEALTH_QUESTIONNAIRE_REQUIRES_INSURANCE_LINE");
    }

    [Fact]
    public void A_health_questionnaire_cannot_be_moved_onto_a_non_insurance_line()
    {
        // Checked in UpdateDetails too, because that call does not go through SetSubjectScope —
        // and a health questionnaire re-scoped onto a savings line would keep collecting health
        // answers under a product that has no business asking.
        var template = Draft();
        template.SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false);

        template.UpdateDetails("Questionnaire santé", null, ProductCategory.Savings)
            .Error.Should().Be("HEALTH_QUESTIONNAIRE_REQUIRES_INSURANCE_LINE");

        template.ProductCategory.Should().Be(ProductCategory.HealthInsurance,
            "a refused update moves nothing");
    }

    // ── ExcludeFromScoring is forced, not defaulted ───────────────────────

    [Fact]
    public void A_health_questionnaire_is_always_excluded_from_scoring()
    {
        var template = Draft();

        // Asked for FALSE, deliberately: BNF-B-07 makes this non-negotiable, so an administrator
        // who unticks it must not be obeyed.
        template.SetSubjectScope(
            QualificationSubjectScope.EachInsuredPerson,
            isHealthQuestionnaire: true,
            excludeFromScoring: false);

        template.ExcludeFromScoring.Should().BeTrue();
    }

    [Fact]
    public void A_non_health_questionnaire_may_be_excluded_or_not()
    {
        var excluded = Draft();
        excluded.SetSubjectScope(QualificationSubjectScope.PrincipalOnly, false, true);
        excluded.ExcludeFromScoring.Should().BeTrue();

        var scored = Draft();
        scored.SetSubjectScope(QualificationSubjectScope.PrincipalOnly, false, false);
        scored.ExcludeFromScoring.Should().BeFalse();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    [Fact]
    public void Only_a_draft_can_be_rescoped()
    {
        var template = Draft();
        template.AddQuestion("Fumez-vous ?", QuestionType.YesNo, 10, true);
        template.Publish(Now);

        template.SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false)
            .Error.Should().Be("TEMPLATE_NOT_IN_DRAFT_STATUS");
    }

    [Fact]
    public void A_duplicate_carries_the_scope_and_the_health_flag()
    {
        // The most dangerous possible default would be a duplicated health questionnaire coming
        // back as a scoring one answered per lead — the copy exists to start the next revision of
        // THIS questionnaire.
        var template = Draft();
        template.AddQuestion("Fumez-vous ?", QuestionType.YesNo, 10, true);
        template.SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false);

        var copy = template.Duplicate("Questionnaire santé v2", Now);

        copy.SubjectScope.Should().Be(QualificationSubjectScope.EachInsuredPerson);
        copy.IsHealthQuestionnaire.Should().BeTrue();
        copy.ExcludeFromScoring.Should().BeTrue();
        copy.Status.Should().Be(TemplateStatus.Draft);
    }
}
