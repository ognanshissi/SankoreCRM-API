using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.Users.Register;

public sealed class RegisterValidation : AbstractValidator<RegisterCommand>
{
    public RegisterValidation(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress()
            .WithMessage(_ => localizer["Register.Email.Required"]);
        RuleFor(x => x.Password).NotEmpty()
            .WithMessage(_ => localizer["Register.Password.Required"])
            .Length(1, 100);
        RuleFor(x => x.ConfirmPassword).NotEmpty()
            .WithMessage(_ => localizer["Register.ConfirmPassword.Required"]);
        RuleFor(x => x.TenantId).NotEmpty()
            .WithMessage(_ => localizer["Register.TenantId.Required"]);
    }
}
