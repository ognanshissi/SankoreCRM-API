using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.Authentication.ResetPassword;

internal sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage(_ => localizer["Auth.UserId.Required"])
            .Must(id => Guid.TryParse(id, out _)).WithMessage(_ => localizer["Auth.UserId.InvalidGuid"]);

        RuleFor(x => x.Token)
            .NotEmpty().WithMessage(_ => localizer["Auth.ResetToken.Required"]);

        RuleFor(x => x.NewPassword)
            .NotEmpty().WithMessage(_ => localizer["Auth.Password.Required"])
            .MinimumLength(8).WithMessage(_ => localizer["Auth.Password.MinLength"])
            .Matches(@"\d").WithMessage(_ => localizer["Auth.Password.Digit"]);

        RuleFor(x => x.ConfirmPassword)
            .Equal(x => x.NewPassword).WithMessage(_ => localizer["Auth.Passwords.Mismatch"]);
    }
}