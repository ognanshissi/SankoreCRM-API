namespace Sankore.Modules.Leads.Features.LeadSources.SetSecret;

using FluentValidation;

internal sealed class SetSecretValidator : AbstractValidator<SetSecretCommand>
{
    public SetSecretValidator()
    {
        RuleFor(x => x.SourceId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Value).NotEmpty();
    }
}
