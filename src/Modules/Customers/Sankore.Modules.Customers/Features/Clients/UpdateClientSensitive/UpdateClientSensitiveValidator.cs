namespace Sankore.Modules.Customers.Features.Clients.UpdateClientSensitive;

using FluentValidation;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// The motive is the load-bearing rule here, so its failure message IS the contract's
/// error code (<c>REASON_REQUIRED</c>): the front-end localises the same string whether
/// the rejection came from the validator (400) or from the handler.
///
/// The 10-character floor exists to keep "ok" / "maj" out of a compliance trail that an
/// auditor will read years later; the 500-character ceiling matches the column.
/// </summary>
public sealed class UpdateClientSensitiveValidator : AbstractValidator<UpdateClientSensitiveCommand>
{
    public UpdateClientSensitiveValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty();

        RuleFor(x => x.Reason)
            .NotEmpty()
            .WithMessage(CustomerErrors.ReasonRequired)
            .MinimumLength(10)
            .WithMessage(CustomerErrors.ReasonRequired)
            .MaximumLength(500);

        RuleFor(x => x.FirstName)
            .MaximumLength(150)
            .When(x => x.FirstName is not null);

        RuleFor(x => x.LastName)
            .MaximumLength(150)
            .When(x => x.LastName is not null);

        RuleFor(x => x.MaidenName).MaximumLength(150);

        RuleFor(x => x.IdentityDocumentType)
            .IsInEnum()
            .When(x => x.IdentityDocumentType.HasValue);

        RuleFor(x => x.IdentityDocumentNumber)
            .NotEmpty()
            .MaximumLength(60)
            .When(x => x.IdentityDocumentNumber is not null);

        RuleFor(x => x.IdentityDocumentExpiresOn)
            .GreaterThan(x => x.IdentityDocumentIssuedOn!.Value)
            .When(x => x.IdentityDocumentIssuedOn.HasValue && x.IdentityDocumentExpiresOn.HasValue)
            .WithMessage("The document expiry date must follow its issue date.");

        When(x => x.Address is not null, () =>
        {
            RuleFor(x => x.Address!.Street).MaximumLength(300);
            RuleFor(x => x.Address!.City).MaximumLength(150);
            RuleFor(x => x.Address!.State).MaximumLength(150);
            RuleFor(x => x.Address!.Country).MaximumLength(100);
            RuleFor(x => x.Address!.ZipCode).MaximumLength(30);
        });
    }
}
