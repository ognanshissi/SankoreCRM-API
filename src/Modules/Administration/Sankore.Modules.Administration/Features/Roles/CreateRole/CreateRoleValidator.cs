using FluentValidation;

namespace Sankore.Modules.Administration.Features.Roles.CreateRole;

internal sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(50)
            .Matches(@"^[A-Za-z][A-Za-z0-9_]*$")
            .WithMessage("Role name must start with a letter and contain only letters, digits, or underscores.");

        RuleFor(x => x.Label)
            .NotEmpty()
            .MaximumLength(100);
    }
}
