namespace Sankore.Modules.Customers.Features.Lifecycle.SuspendClient;

using FluentValidation;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// Shape validation only. The handler re-checks the motive and returns
/// <see cref="CustomerErrors.ReasonRequired"/> as a Result code, because the
/// pipeline's ValidationBehavior throws a ValidationException (HTTP 400) instead
/// of a typed error code — callers driven by error codes must still get
/// <c>REASON_REQUIRED</c> when the command is dispatched in-process.
/// </summary>
public sealed class SuspendClientValidator : AbstractValidator<SuspendClientCommand>
{
    public SuspendClientValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage(CustomerErrors.ReasonRequired)
            .MaximumLength(1000);
    }
}
