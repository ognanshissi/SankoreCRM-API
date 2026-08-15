using FluentValidation;

namespace Sankore.Modules.Users.Features.Login;

public sealed class LoginValidation : AbstractValidator<LoginCommand>
{
    public LoginValidation()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}
