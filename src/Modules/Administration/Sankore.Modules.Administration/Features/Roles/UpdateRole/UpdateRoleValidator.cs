using FluentValidation;

namespace Sankore.Modules.Administration.Features.Roles.UpdateRole;

internal sealed class UpdateRoleValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(100);
    }
}
