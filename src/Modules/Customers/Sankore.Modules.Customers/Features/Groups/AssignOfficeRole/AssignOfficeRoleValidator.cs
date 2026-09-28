namespace Sankore.Modules.Customers.Features.Groups.AssignOfficeRole;

using FluentValidation;

public sealed class AssignOfficeRoleValidator : AbstractValidator<AssignOfficeRoleCommand>
{
    public AssignOfficeRoleValidator()
    {
        RuleFor(x => x.GroupId).NotEmpty();
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.OfficeRole).IsInEnum();
    }
}
