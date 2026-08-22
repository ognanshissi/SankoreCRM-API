using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.CreateUser;

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.AgencyId).NotEmpty();
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.DefaultLanguage).NotEmpty().MaximumLength(10);
        RuleFor(x => x.CallerUserId).NotEmpty();
    }
}
