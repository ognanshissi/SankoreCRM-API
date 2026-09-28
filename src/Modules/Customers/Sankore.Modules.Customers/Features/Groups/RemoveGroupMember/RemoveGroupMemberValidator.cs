namespace Sankore.Modules.Customers.Features.Groups.RemoveGroupMember;

using FluentValidation;

public sealed class RemoveGroupMemberValidator : AbstractValidator<RemoveGroupMemberCommand>
{
    public RemoveGroupMemberValidator()
    {
        RuleFor(x => x.GroupId).NotEmpty();
        RuleFor(x => x.ClientId).NotEmpty();

        // The motive is what the audit trail and the group's history will show;
        // the handler re-checks it so a non-HTTP caller cannot bypass the rule.
        // The message is attached to each rule, not to the chain: FluentValidation
        // applies a trailing WithMessage to the PRECEDING validator only, so a single
        // one at the end would leave a blank motive with the default message.
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("REASON_REQUIRED")
            .MinimumLength(3).WithMessage("REASON_REQUIRED")
            .MaximumLength(1000);
    }
}
