using FluentValidation;

namespace Sankore.Modules.Administration.Features.Products.LinkProductToCbs;

internal sealed class LinkProductToCbsValidator : AbstractValidator<LinkProductToCbsCommand>
{
    public LinkProductToCbsValidator()
    {
        RuleFor(x => x.BusinessProductId)
            .NotEmpty().WithMessage("CBS product identifier is required.")
            .MaximumLength(100);

        RuleFor(x => x.BusinessPlatformName)
            .NotEmpty().WithMessage("CBS platform name is required.")
            .MaximumLength(100);
    }
}
