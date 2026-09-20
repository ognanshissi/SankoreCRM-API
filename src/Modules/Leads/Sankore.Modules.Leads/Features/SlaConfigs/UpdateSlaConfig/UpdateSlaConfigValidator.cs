namespace Sankore.Modules.Leads.Features.SlaConfigs.UpdateSlaConfig;

using FluentValidation;

internal sealed class UpdateSlaConfigValidator : AbstractValidator<UpdateSlaConfigCommand>
{
    public UpdateSlaConfigValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.FirstContactDeadline).GreaterThan(TimeSpan.Zero);
        RuleFor(x => x.QualificationDeadline).GreaterThan(TimeSpan.Zero);
        RuleFor(x => x.FollowUpDeadline).GreaterThan(TimeSpan.Zero);
        RuleFor(x => x.EscalationDeadline).GreaterThan(TimeSpan.Zero);
    }
}
