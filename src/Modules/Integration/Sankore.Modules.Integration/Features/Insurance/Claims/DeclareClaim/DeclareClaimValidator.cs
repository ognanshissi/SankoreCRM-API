namespace Sankore.Modules.Integration.Features.Insurance.Claims.DeclareClaim;

using FluentValidation;

/// <summary>
/// Shape only. Whether the loss falls inside the cover is the HANDLER's, because it needs the
/// policy's own dates and the platform's clock — neither of which a validator may hold.
/// </summary>
internal sealed class DeclareClaimValidator : AbstractValidator<DeclareClaimCommand>
{
    public DeclareClaimValidator()
    {
        RuleFor(c => c.PolicyId).NotEmpty().OverridePropertyName("policyId");

        RuleFor(c => c.OccurredOn)
            .NotEqual(default(DateOnly))
            .WithMessage("The date of the loss is required.")
            .OverridePropertyName("occurredOn");

        // In the index AND in the command's idempotency key, which is why it is required and
        // capped: it is the discriminator that lets a fire and a theft on the same policy on the
        // same day be two dossiers rather than one.
        RuleFor(c => c.Nature)
            .NotEmpty()
            .MaximumLength(100)
            .WithMessage("The nature of the loss is required, 100 characters at most.")
            .OverridePropertyName("nature");

        // Uncapped in the column — it is encrypted text, and the ciphertext grows with the
        // account — but a request is still bounded: an unbounded free-text field reachable by an
        // authenticated agent is a way to fill a disk.
        RuleFor(c => c.Description)
            .MaximumLength(4000)
            .When(c => c.Description is not null)
            .OverridePropertyName("description");
    }
}
