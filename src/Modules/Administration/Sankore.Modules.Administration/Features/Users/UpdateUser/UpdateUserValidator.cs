using FluentValidation;

namespace Sankore.Modules.Administration.Features.Users.UpdateUser;

internal sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        When(x => x.FullName is not null, () =>
            RuleFor(x => x.FullName).NotEmpty().MaximumLength(200));

        When(x => x.SpokenLanguages is not null, () =>
            RuleFor(x => x.SpokenLanguages).NotEmpty());

        RuleFor(x => x.EmployeeNumber).MaximumLength(50);
        When(x => x.FirstName is not null, () => RuleFor(x => x.FirstName).NotEmpty().MaximumLength(200));
        When(x => x.LastName is not null, () => RuleFor(x => x.LastName).NotEmpty().MaximumLength(200));
        When(x => x.Email is not null, () => RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256));
        RuleFor(x => x.WorkPhone).MaximumLength(30).Matches(@"^\+?[0-9 ().-]*$")
            .WithMessage("Le téléphone professionnel n'est pas un numéro valide.");
    }
}
