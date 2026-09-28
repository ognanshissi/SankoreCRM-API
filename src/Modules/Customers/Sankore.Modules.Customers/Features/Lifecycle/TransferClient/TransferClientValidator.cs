namespace Sankore.Modules.Customers.Features.Lifecycle.TransferClient;

using FluentValidation;
using Sankore.Modules.Customers.Domain;

public sealed class TransferClientValidator : AbstractValidator<TransferClientCommand>
{
    public TransferClientValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();
        RuleFor(x => x.TargetAgencyId).NotEmpty();

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage(CustomerErrors.ReasonRequired)
            .MaximumLength(1000);
    }
}
