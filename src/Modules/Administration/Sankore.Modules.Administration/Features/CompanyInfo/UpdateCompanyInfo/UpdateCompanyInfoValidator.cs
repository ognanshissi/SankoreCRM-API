using FluentValidation;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.CompanyInfo.UpdateCompanyInfo;

internal sealed class UpdateCompanyInfoValidator : AbstractValidator<UpdateCompanyInfoCommand>
{
    public UpdateCompanyInfoValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Description).MaximumLength(200);
        RuleFor(x => x.LogoUrl).MaximumLength(500);
        RuleFor(x => x.PrimaryColor).MaximumLength(10);
        RuleFor(x => x.SecondaryColor).MaximumLength(10);
        RuleFor(x => x.DefaultLanguage).IsInEnum();
        RuleFor(x => x.Currency)
            .Must(InstitutionCurrencies.IsSupported)
            .When(x => x.Currency is not null)
            .WithMessage("Devise non prise en charge.");
    }
}
