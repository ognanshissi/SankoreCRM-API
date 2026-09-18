namespace Sankore.Modules.Leads.Features.MergeLeads;

using FluentValidation;

internal sealed class MergeLeadsValidator : AbstractValidator<MergeLeadsCommand>
{
    public MergeLeadsValidator()
    {
        RuleFor(x => x.TargetLeadId).NotEmpty();
        RuleFor(x => x.SourceLeadId).NotEmpty();
        RuleFor(x => x.MergedBy).NotEmpty();
        RuleFor(x => x)
            .Must(x => x.TargetLeadId != x.SourceLeadId)
            .WithMessage("Target and source leads must be different.");
    }
}
