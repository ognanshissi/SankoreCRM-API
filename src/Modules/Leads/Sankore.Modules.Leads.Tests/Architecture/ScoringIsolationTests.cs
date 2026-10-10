namespace Sankore.Modules.Leads.Tests.Architecture;

using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

/// <summary>
/// Pins the isolation BNF-B-09 requires: a health questionnaire answer must be provably unreachable
/// from lead scoring and from dispatching.
///
/// <para>
/// <b>Why an architecture test and not a unit test.</b> The requirement is a negative over all
/// future code — "the scorer never reads this" — and no unit test can state it: a test that asserts
/// a score is unaffected by health answers passes just as well on the day somebody starts reading
/// them and happens to weight them at zero. These assertions are over assembly dependencies, read
/// from IL, so they fail on the reference itself rather than on its effect.
/// </para>
///
/// <para>
/// <b>Why it is worth having before the health data exists.</b> Right now
/// <c>LeadScoreCalculator</c> scores from the lead's profile, its source and its activity history
/// and does not touch <c>QualificationResponse</c> at all. That is a property of the current code,
/// not a guarantee — and the cheapest moment to pin a property is while it already holds. When
/// Lot 4 adds <c>SubjectType</c>, <c>IsHealthQuestionnaire</c> and the encrypted answers, this file
/// is what stops the obvious shortcut ("the scorer already has the lead, just load its responses").
/// </para>
///
/// <para>
/// Lot 4 added the rule Lot 0 could not express: <c>DecryptedHealthAnswers</c> now exists as a
/// named type, so "nothing outside the questionnaire slice may hold decrypted health answers" is
/// checkable. Asserting over <c>QualificationResponse</c> as a whole could never have stood in for
/// it — non-health template answers legitimately feed the score through <c>QualifyLeadHandler</c>,
/// which is why the rules below name the scorer and the dispatcher rather than "anything that
/// scores".
/// </para>
/// </summary>
public sealed class ScoringIsolationTests
{
    private const string QualificationResponse = "Sankore.Modules.Leads.Domain.QualificationResponse";
    private const string DispatchNamespace = "Sankore.Modules.Leads.Features.DispatchLead";

    /// <summary>
    /// The only shape in which health answers leave persistence in clear (BNF-B-09). A plain
    /// <c>IReadOnlyList&lt;(Guid, string)&gt;</c> would have been indistinguishable from any other
    /// answer list, so no rule could have named it — which is why the type exists.
    /// </summary>
    private const string DecryptedHealthAnswers =
        "Sankore.Modules.Leads.Features.Questionnaires.DecryptedHealthAnswers";

    private const string HealthAnswerAccess =
        "Sankore.Modules.Leads.Features.Questionnaires.HealthAnswerAccess";

    /// <summary>The slice allowed to hold them.</summary>
    private const string QuestionnaireNamespace = "Sankore.Modules.Leads.Features.Questionnaires";

    private static Types LeadsAssembly => Types.InAssembly(typeof(LeadsModule).Assembly);

    [Fact]
    public void Lead_scoring_does_not_depend_on_qualification_responses()
    {
        // LeadScoreCalculator is the F13.6 scorer. Template answers reach a score through
        // QualifyLeadHandler, which computes them from the submitted payload and the template —
        // never by reading back a stored response. Keeping the calculator free of the entity is
        // what makes "the scorer cannot see a stored answer" a structural fact.
        var result = LeadsAssembly
            .That().HaveNameStartingWith("LeadScoreCalculator")
            .ShouldNot().HaveDependencyOn(QualificationResponse)
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "LeadScoreCalculator must score from the lead, its source and its activities only — "
            + "reading a stored QualificationResponse would put health answers one field away "
            + "from the score (BNF-B-09).");
        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Dispatching_does_not_depend_on_qualification_responses()
    {
        // Routing decides which agent receives a lead. It may read the score, the agency, the
        // product and the PEP exposure flag; a questionnaire answer is not a routing input, and a
        // health answer reaching a dispatching strategy would put special-category data into a
        // decision nobody audits per-field.
        var result = LeadsAssembly
            .That().ResideInNamespaceStartingWith(DispatchNamespace)
            .ShouldNot().HaveDependencyOn(QualificationResponse)
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "No dispatching type may reference QualificationResponse (BNF-B-09).");
        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Nothing_outside_the_questionnaire_slice_holds_decrypted_health_answers()
    {
        // The rule BNF-B-09 asks for, in the only form that can actually be checked: not "health
        // data is not used for scoring" — which no test can state about future code — but "the
        // type that carries it in clear is unreachable from anywhere but its own slice".
        var result = LeadsAssembly
            .That().DoNotResideInNamespaceStartingWith(QuestionnaireNamespace)
            .ShouldNot().HaveDependencyOnAny(DecryptedHealthAnswers, HealthAnswerAccess)
            .GetResult();

        result.FailingTypeNames.Should().BeNullOrEmpty(
            "decrypted health answers may only be held inside Features/Questionnaires, where the "
            + "lead:health:read gate lives (BNF-B-09). If a new feature genuinely needs them, it "
            + "belongs in that slice or behind a projection that drops the values.");
        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Lead_scoring_does_not_depend_on_decrypted_health_answers()
    {
        // Implied by the rule above, and asserted separately because this is the specific claim
        // the specification makes — a reader looking for "health never reaches the score" should
        // find a test that says exactly that.
        var result = LeadsAssembly
            .That().HaveNameStartingWith("LeadScoreCalculator")
            .ShouldNot().HaveDependencyOnAny(DecryptedHealthAnswers, HealthAnswerAccess)
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Dispatching_does_not_depend_on_decrypted_health_answers()
    {
        var result = LeadsAssembly
            .That().ResideInNamespaceStartingWith(DispatchNamespace)
            .ShouldNot().HaveDependencyOnAny(DecryptedHealthAnswers, HealthAnswerAccess)
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void The_health_rule_has_teeth_the_questionnaire_slice_really_does_hold_them()
    {
        // The guard against a VACUOUS architecture rule. "Nothing outside X depends on Y" passes
        // trivially if nothing anywhere depends on Y — a renamed type, a deleted feature, a typo
        // in the namespace constant. This asserts the exclusion is load-bearing: inside the slice,
        // the dependency exists, so excluding the slice is what makes the rule say something.
        var inside = LeadsAssembly
            .That().ResideInNamespaceStartingWith(QuestionnaireNamespace)
            .And().HaveDependencyOnAny(DecryptedHealthAnswers, HealthAnswerAccess)
            .GetTypes();

        inside.Should().NotBeEmpty(
            "if no type in Features/Questionnaires holds decrypted health answers, the rule above "
            + "is passing over a dependency that no longer exists");
    }

    [Fact]
    public void The_rules_above_are_evaluated_against_a_populated_assembly()
    {
        // NetArchTest reports success for an empty selection, so a renamed class or a moved
        // namespace would turn both rules above green while checking nothing. This is the guard
        // against that: it asserts the selectors still match something.
        LeadsAssembly.That().HaveNameStartingWith("LeadScoreCalculator").GetTypes()
            .Should().NotBeEmpty("the scorer was renamed — update ScoringIsolationTests");

        LeadsAssembly.That().ResideInNamespaceStartingWith(DispatchNamespace).GetTypes()
            .Should().NotBeEmpty("the dispatching namespace moved — update ScoringIsolationTests");

        LeadsAssembly.That().ResideInNamespaceStartingWith(QuestionnaireNamespace).GetTypes()
            .Should().NotBeEmpty("the questionnaire slice moved — update ScoringIsolationTests");

        // And the guarded types still exist under the names the rules spell. A renamed
        // DecryptedHealthAnswers would make every rule above pass over a dependency on nothing.
        LeadsAssembly.That().HaveNameStartingWith("DecryptedHealthAnswers").GetTypes()
            .Should().NotBeEmpty("DecryptedHealthAnswers was renamed — update ScoringIsolationTests");
        LeadsAssembly.That().HaveNameStartingWith("HealthAnswerAccess").GetTypes()
            .Should().NotBeEmpty("HealthAnswerAccess was renamed — update ScoringIsolationTests");
    }
}
