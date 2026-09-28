namespace Sankore.Modules.Customers.Features.Clients.RevealSensitiveField;

using FluentValidation;
using Sankore.Modules.Customers.Domain;

public sealed class RevealSensitiveFieldValidator : AbstractValidator<RevealSensitiveFieldCommand>
{
    public RevealSensitiveFieldValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        // A value outside the enum is rejected here as a 400; a value that IS in the enum
        // but has nothing stored for this client is a business outcome and comes back from
        // the handler as UNKNOWN_SENSITIVE_FIELD.
        RuleFor(x => x.Field)
            .IsInEnum()
            .WithMessage(CustomerErrors.UnknownSensitiveField);
    }
}
