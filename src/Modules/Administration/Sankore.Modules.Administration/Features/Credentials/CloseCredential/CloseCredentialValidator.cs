namespace Sankore.Modules.Administration.Features.Credentials.CloseCredential;

using FluentValidation;

internal sealed class CloseCredentialValidator : AbstractValidator<CloseCredentialCommand>
{
    public CloseCredentialValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        // Re-checked in the aggregate, which is what a future caller reaching it outside the
        // pipeline will hit: "the card is gone" and "the card was suspended" are not the same
        // fact to a controller.
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}
