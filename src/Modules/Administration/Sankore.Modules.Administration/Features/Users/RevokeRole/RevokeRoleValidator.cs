using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.RevokeRole;

public sealed class RevokeRoleValidator : AbstractValidator<RevokeRoleCommand>
{
    public RevokeRoleValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RoleId).NotEmpty();
    }
}
