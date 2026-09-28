namespace Sankore.Modules.Customers.Features.Compliance.Retention.AnonymizeClient;

using FluentValidation;

public sealed class AnonymizeClientValidator : AbstractValidator<AnonymizeClientCommand>
{
    public AnonymizeClientValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        // The handler re-checks emptiness and answers REASON_REQUIRED, so a dispatch that does
        // not go through the HTTP pipeline is covered too. Ten characters minimum: "ok" is not a
        // justification for an irreversible erasure.
        RuleFor(x => x.Reason)
            .NotEmpty()
            .MinimumLength(10)
            .MaximumLength(500);
    }
}
