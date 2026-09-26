namespace Sankore.Modules.Leads.Features.Import.ImportFromGoogleContacts;

using FluentValidation;

public sealed class ImportFromGoogleContactsValidator
    : AbstractValidator<ImportFromGoogleContactsCommand>
{
    public ImportFromGoogleContactsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.InitiatedBy).NotEmpty();
        RuleFor(x => x.Defaults.InterestedProduct)
            .NotEmpty()
            .WithName("defaults.interestedProduct")
            .WithMessage("InterestedProduct is required: Google contacts carry no product.");
    }
}
