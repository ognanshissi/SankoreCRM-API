namespace Sankore.Modules.Leads.Tests.Features.QualificationTemplates;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.QualificationTemplates.CreateQualificationTemplate;
using Sankore.Modules.Leads.Features.Questionnaires;
using Xunit;

/// <summary>
/// Conditional questions (recette du 2026-10-26) : a rule saved twice must still point at its
/// trigger, and a question shown only on some answer must not be required on the others.
/// </summary>
public sealed class QuestionnaireRuleWiringTests
{
    private static readonly Guid ReadQ1 = Guid.NewGuid();
    private static readonly Guid ReadQ2 = Guid.NewGuid();

    private static QualificationTemplate Build(params QuestionInput[] questions)
    {
        var template = QualificationTemplate.Create(Guid.NewGuid(), "Obsèques", DateTimeOffset.UtcNow);
        var result = CreateQualificationTemplateHandler.BuildQuestionsAndSections(template, null, questions);
        result.IsSuccess.Should().BeTrue();
        return template;
    }

    private static QuestionInput Contract(Guid? id = null)
        => new("Contrat obsèques ?", QuestionType.SingleChoice, 1, IsRequired: true, Options: ["Oui", "Non"], Id: id);

    private static QuestionInput Insurer(Guid trigger, Guid? id = null)
        => new("Assureur ?", QuestionType.Text, 1, IsRequired: true, Id: id,
            Rules: [new RuleInput(trigger, "Oui", QuestionRuleAction.Show)]);

    [Fact]
    public void A_rule_is_rewired_onto_the_new_id_of_its_trigger()
    {
        var template = Build(Contract(ReadQ1), Insurer(ReadQ1, ReadQ2));

        var contract = template.Questions[0];
        template.Questions[1].GetRules().Should().ContainSingle()
            .Which.TriggerQuestionId.Should().Be(contract.Id);
        contract.Id.Should().NotBe(ReadQ1, "a save recreates every question");
    }

    [Fact]
    public void A_rule_naming_no_question_of_the_request_is_dropped()
    {
        // Left as it was, a « Show » rule on a vanished trigger hides its question for good.
        var template = Build(Contract(ReadQ1), Insurer(Guid.NewGuid(), ReadQ2));

        template.Questions[1].GetRules().Should().BeEmpty();
    }

    [Fact]
    public void A_question_shown_on_yes_is_hidden_and_not_required_on_no()
    {
        var template = Build(Contract(ReadQ1), Insurer(ReadQ1, ReadQ2));
        var (contract, insurer) = (template.Questions[0], template.Questions[1]);

        var no = new Dictionary<Guid, string> { [contract.Id] = "Non" };
        QuestionnaireRules.HiddenQuestionIds(template.Questions, no).Should().Contain(insurer.Id);
        QuestionnaireRules.MissingRequired(template, no).Should().BeEmpty();

        var yes = new Dictionary<Guid, string> { [contract.Id] = "oui" };
        QuestionnaireRules.HiddenQuestionIds(template.Questions, yes).Should().NotContain(insurer.Id);
        QuestionnaireRules.MissingRequired(template, yes).Should().ContainSingle()
            .Which.Id.Should().Be(insurer.Id);
    }

    [Fact]
    public void An_unanswered_trigger_keeps_the_shown_question_hidden()
    {
        var template = Build(Contract(ReadQ1), Insurer(ReadQ1, ReadQ2));

        QuestionnaireRules.MissingRequired(template, new Dictionary<Guid, string>())
            .Select(q => q.Label).Should().Equal("Contrat obsèques ?");
    }
}
