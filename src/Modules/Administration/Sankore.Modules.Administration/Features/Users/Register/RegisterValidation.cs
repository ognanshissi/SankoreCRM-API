using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.Register;

public sealed class RegisterValidation: AbstractValidator<RegisterCommand>
{
    public RegisterValidation()
    {
        RuleFor(x => x.Email).NotEmpty()
            .EmailAddress()
            .WithMessage("Email is required");

        RuleFor(x => x.Password).NotEmpty().WithMessage("Password is required").Length(1, 100);
    }
}