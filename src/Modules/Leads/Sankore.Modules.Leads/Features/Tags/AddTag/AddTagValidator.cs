namespace Sankore.Modules.Leads.Features.Tags.AddTag;

using FluentValidation;

internal sealed class AddTagValidator : AbstractValidator<AddTagCommand>
{
    public AddTagValidator()
    {
        RuleFor(x => x.Tag)
            .NotEmpty()
            .MaximumLength(50)
            .Matches(@"^[a-zA-Z0-9\-_]+$")
            .WithMessage("Tag must contain only letters, numbers, hyphens and underscores.");

        RuleFor(x => x.AddedBy).NotEmpty();
    }
}
