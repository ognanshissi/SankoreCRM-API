using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.AssignScopedPermission;

public sealed class AssignScopedPermissionValidator : AbstractValidator<AssignScopedPermissionCommand>
{
    public AssignScopedPermissionValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.PermissionCode).NotEmpty().MaximumLength(100);
        RuleFor(x => x.StartDate).NotEmpty();
        RuleFor(x => x.EndDate)
            .NotEmpty()
            .GreaterThan(x => x.StartDate)
            .WithMessage("EndDate must be after StartDate.");
        RuleFor(x => x.ScopeType)
            .Must(t => t is null || t is "Agency" or "Territory" or "Tenant")
            .WithMessage("ScopeType must be Agency, Territory, Tenant, or null.");
        RuleFor(x => x.ScopeId)
            .NotEmpty()
            .When(x => x.ScopeType is not null)
            .WithMessage("ScopeId is required when ScopeType is specified.");
    }
}
