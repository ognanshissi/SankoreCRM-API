using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.Users.AdminResetPassword;

internal sealed class AdminResetPasswordValidator : AbstractValidator<AdminResetPasswordCommand>
{
    public AdminResetPasswordValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.TargetUserId)
            .NotEmpty().WithMessage("Target user id is required.");

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(_ => localizer["Auth.Password.Required"])
            .MinimumLength(8).WithMessage(_ => localizer["Auth.Password.MinLength"])
            .Matches(@"\d").WithMessage(_ => localizer["Auth.Password.Digit"]);

        RuleFor(x => x.ConfirmPassword)
            .Equal(x => x.NewPassword).WithMessage(_ => localizer["Auth.Passwords.Mismatch"]);
    }
}
