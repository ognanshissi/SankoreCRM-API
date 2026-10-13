namespace Sankore.Modules.Leads.Features.Import.ImportFromGoogleContacts;

using FluentValidation;
using Sankore.Shared.Infrastructure.Validation;

public sealed class ImportFromGoogleContactsValidator
    : AbstractValidator<ImportFromGoogleContactsCommand>
{
    public ImportFromGoogleContactsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.InitiatedBy).NotEmpty();
        RuleFor(x => x.Defaults.InterestedProduct)
            .NotEmpty()
            .WithClientField("defaults.interestedProduct")
            .WithMessage("InterestedProduct is required: Google contacts carry no product.");
    }
}
