using FluentValidation;

namespace Sankore.Modules.Administration.Features.Roles.AssignPermissionToRole;

internal sealed class AssignPermissionToRoleValidator : AbstractValidator<AssignPermissionToRoleCommand>
{
    public AssignPermissionToRoleValidator()
    {
        RuleFor(x => x.PermissionCode).NotEmpty().MaximumLength(100);
    }
}
