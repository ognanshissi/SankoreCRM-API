namespace Sankore.Modules.Customers.Features.Clients.UpdateClient;

using FluentValidation;

public sealed class UpdateClientValidator : AbstractValidator<UpdateClientCommand>
{
    public UpdateClientValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        RuleFor(x => x.Profession).MaximumLength(150);
        RuleFor(x => x.Employer).MaximumLength(150);
        RuleFor(x => x.MaritalStatus).IsInEnum().When(x => x.MaritalStatus.HasValue);

        RuleFor(x => x.DeclaredIncome)
            .GreaterThanOrEqualTo(0)
            .When(x => x.DeclaredIncome.HasValue);

        RuleFor(x => x.DeclaredIncomeCurrency)
            .Length(3)
            .When(x => !string.IsNullOrWhiteSpace(x.DeclaredIncomeCurrency));

        RuleFor(x => x.PreferredLanguage).MaximumLength(10);
    }
}
