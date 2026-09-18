using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.Users.ChangePassword;

internal sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(_ => localizer["Auth.Password.Required"])
            .MinimumLength(8).WithMessage(_ => localizer["Auth.Password.MinLength"])
            .Matches(@"\d").WithMessage(_ => localizer["Auth.Password.Digit"]);

        RuleFor(x => x.ConfirmPassword)
            .Equal(x => x.NewPassword).WithMessage(_ => localizer["Auth.Passwords.Mismatch"]);
    }
}
