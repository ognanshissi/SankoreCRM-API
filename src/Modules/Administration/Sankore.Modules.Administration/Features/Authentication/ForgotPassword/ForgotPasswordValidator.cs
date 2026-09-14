using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ForgotPassword;

internal sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator(
        ITenantContext tenantContext,
        IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage(_ => localizer["Auth.Email.Required"]);
    }
}
