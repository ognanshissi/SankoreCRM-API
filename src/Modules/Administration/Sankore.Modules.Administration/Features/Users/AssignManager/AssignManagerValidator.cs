using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.AssignManager;

public sealed class AssignManagerValidator : AbstractValidator<AssignManagerCommand>
{
    public AssignManagerValidator()
    {
        RuleFor(x => x.UserId).NotEqual(Guid.Empty);
        RuleFor(x => x.ManagerUserId).NotEqual(Guid.Empty);

        RuleFor(x => x.ManagerUserId)
            .NotEqual(x => x.UserId)
            .WithMessage(AssignManagerErrors.SelfReportForbidden);
    }
}
