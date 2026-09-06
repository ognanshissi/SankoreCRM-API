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
    }
}
