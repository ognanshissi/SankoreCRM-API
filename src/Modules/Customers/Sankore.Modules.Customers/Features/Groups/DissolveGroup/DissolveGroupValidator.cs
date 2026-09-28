namespace Sankore.Modules.Customers.Features.Groups.DissolveGroup;

using FluentValidation;

public sealed class DissolveGroupValidator : AbstractValidator<DissolveGroupCommand>
{
    public DissolveGroupValidator()
    {
        RuleFor(x => x.GroupId).NotEmpty();

        // The message is attached to each rule, not to the chain: FluentValidation
        // applies a trailing WithMessage to the PRECEDING validator only, so a single
        // one at the end would leave a blank motive with the default message.
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("REASON_REQUIRED")
            .MinimumLength(3).WithMessage("REASON_REQUIRED")
            .MaximumLength(1000);
    }
}
