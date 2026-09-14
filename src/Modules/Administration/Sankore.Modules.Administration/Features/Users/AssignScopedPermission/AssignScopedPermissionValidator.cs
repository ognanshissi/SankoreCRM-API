using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.Users.AssignScopedPermission;

public sealed class AssignScopedPermissionValidator : AbstractValidator<AssignScopedPermissionCommand>
{
    public AssignScopedPermissionValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PermissionCode).NotEmpty().MaximumLength(100);
        RuleFor(x => x.StartDate).NotEmpty();
        RuleFor(x => x.EndDate)
            .NotEmpty()
            .GreaterThan(x => x.StartDate)
            .WithMessage(_ => localizer["Permission.EndDate.AfterStartDate"]);
        RuleFor(x => x.ScopeType)
            .Must(t => t is null || t is "Agency" or "Territory" or "Tenant")
            .WithMessage(_ => localizer["Permission.ScopeType.Invalid"]);
        RuleFor(x => x.ScopeId)
            .NotEmpty()
            .When(x => x.ScopeType is not null)
            .WithMessage(_ => localizer["Permission.ScopeId.Required"]);
    }
}
