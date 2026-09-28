namespace Sankore.Modules.Customers.Features.Lifecycle.ReactivateClient;

using FluentValidation;

public sealed class ReactivateClientValidator : AbstractValidator<ReactivateClientCommand>
{
    public ReactivateClientValidator() => RuleFor(x => x.ClientId).NotEmpty();
}
