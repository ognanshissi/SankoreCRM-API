namespace Sankore.Modules.Customers.Features.Lifecycle.ArchiveClient;

using FluentValidation;
using Sankore.Modules.Customers.Domain;

public sealed class ArchiveClientValidator : AbstractValidator<ArchiveClientCommand>
{
    public ArchiveClientValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage(CustomerErrors.ReasonRequired)
            .MaximumLength(1000);
    }
}
