namespace Sankore.Modules.Customers.Features.Groups.AddGroupMember;

using FluentValidation;

public sealed class AddGroupMemberValidator : AbstractValidator<AddGroupMemberCommand>
{
    public AddGroupMemberValidator()
    {
        RuleFor(x => x.GroupId).NotEmpty();
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.OfficeRole).IsInEnum();
    }
}
