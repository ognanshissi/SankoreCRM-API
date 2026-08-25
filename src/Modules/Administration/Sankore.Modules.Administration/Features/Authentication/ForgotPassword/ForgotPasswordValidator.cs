using FluentValidation;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ForgotPassword;

internal sealed class ForgotPasswordValidator: AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator(ITenantContext tenantContext)
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Email is required");
    }
}