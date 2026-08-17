using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.Login;

public sealed class LoginValidation : AbstractValidator<LoginCommand>
{
    public LoginValidation()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}
