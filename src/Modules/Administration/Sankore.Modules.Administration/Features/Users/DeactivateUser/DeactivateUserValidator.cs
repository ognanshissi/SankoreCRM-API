using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.DeactivateUser;

public sealed class DeactivateUserValidator : AbstractValidator<DeactivateUserCommand>
{
    public DeactivateUserValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
