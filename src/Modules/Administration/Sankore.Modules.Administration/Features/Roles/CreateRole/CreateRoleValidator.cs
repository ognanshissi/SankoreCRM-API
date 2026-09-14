using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.Roles.CreateRole;

internal sealed class CreateRoleValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(50)
            .Matches(@"^[A-Za-z][A-Za-z0-9_]*$")
            .WithMessage(_ => localizer["Role.Name.Format"]);

        RuleFor(x => x.Label)
            .NotEmpty()
            .MaximumLength(100);
    }
}
