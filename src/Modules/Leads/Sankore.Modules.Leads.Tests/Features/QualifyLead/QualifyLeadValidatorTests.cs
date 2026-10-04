namespace Sankore.Modules.Leads.Tests.Features.QualifyLead;

using FluentAssertions;
using Sankore.Modules.Leads.Features.QualifyLead;
using Xunit;

/// <summary>
/// The validator is the only thing standing between a command and its handler, and it is NOT
/// exercised by the module's end-to-end test: that test builds its own MediatR pipeline without
/// the behaviours, which are registered by the bootstrapper. So a rule that rejects a legitimate
/// caller shows up at runtime, in a consumer, as a MassTransit R-FAULT — which is exactly how
/// `RuleFor(x => x.QualifiedBy).NotEmpty()` was found.
/// </summary>
public sealed class QualifyLeadValidatorTests
{
    private readonly QualifyLeadValidator _validator = new();

    [Fact]
    public void Accepts_the_auto_dispatch_consumer_command_which_has_no_human_actor()
    {
        // Exactly what LeadAutoDispatchConsumer sends: no template, no explicit score, and
        // Guid.Empty for the actor — the SYSTEM placeholder, since nobody clicked anything.
        var result = _validator.Validate(new QualifyLeadCommand(
            LeadId: Guid.NewGuid(),
            QualifiedBy: Guid.Empty,
            TriggerEvent: "AUTO_ON_CAPTURE"));

        result.IsValid.Should().BeTrue(
            "auto-dispatch on capture runs with no human actor; rejecting it faults every "
            + "captured lead in the consumer");
    }

    [Fact]
    public void Still_requires_an_actor_when_answers_are_recorded_against_a_template()
    {
        // The template path is the one that writes QualificationResponse.AnsweredBy, so here an
        // empty actor would persist a response nobody can be held to.
        var result = _validator.Validate(new QualifyLeadCommand(
            LeadId: Guid.NewGuid(),
            QualifiedBy: Guid.Empty,
            TriggerEvent: "MANUAL",
            TemplateId: Guid.NewGuid(),
            Answers: [new QualificationAnswerInput(Guid.NewGuid(), "oui")]));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.PropertyName == nameof(QualifyLeadCommand.QualifiedBy));
    }

    [Fact]
    public void Accepts_a_template_qualification_that_names_its_actor()
    {
        var result = _validator.Validate(new QualifyLeadCommand(
            LeadId: Guid.NewGuid(),
            QualifiedBy: Guid.NewGuid(),
            TriggerEvent: "MANUAL",
            TemplateId: Guid.NewGuid(),
            Answers: [new QualificationAnswerInput(Guid.NewGuid(), "oui")]));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Accepts_a_system_score_override_without_an_actor()
    {
        // Path 2 does not record who scored either, so a background recalculation is legitimate.
        var result = _validator.Validate(new QualifyLeadCommand(
            LeadId: Guid.NewGuid(),
            QualifiedBy: Guid.Empty,
            TriggerEvent: "RECALCULATED",
            Score: 72));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Still_rejects_a_missing_lead_or_trigger()
    {
        var noLead = _validator.Validate(new QualifyLeadCommand(
            LeadId: Guid.Empty, QualifiedBy: Guid.NewGuid(), TriggerEvent: "MANUAL"));
        noLead.IsValid.Should().BeFalse();

        var noTrigger = _validator.Validate(new QualifyLeadCommand(
            LeadId: Guid.NewGuid(), QualifiedBy: Guid.NewGuid(), TriggerEvent: ""));
        noTrigger.IsValid.Should().BeFalse();
    }
}
