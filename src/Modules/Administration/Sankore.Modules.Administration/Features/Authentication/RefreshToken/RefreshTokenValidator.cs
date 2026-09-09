using FluentValidation;

namespace Sankore.Modules.Administration.Features.Authentication.RefreshToken;

internal sealed class RefreshTokenValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
    }
}