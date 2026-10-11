namespace Sankore.Modules.Administration.Features.Credentials.IssueCredential;

using FluentValidation;
using Sankore.Modules.Administration.Domain;

/// <summary>
/// Request-shaped rules. The term invariant (<c>ExpiresOn &gt; IssuedOn</c>) lives in
/// <c>DistributionCredential.Issue</c> and throws, because a credential that was never valid for
/// a day has no reason to exist — it is repeated here only so the caller gets a 400 that names
/// the field instead of a 500.
/// </summary>
internal sealed class IssueCredentialValidator : AbstractValidator<IssueCredentialCommand>
{
    public IssueCredentialValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        RuleFor(x => x.Category)
            .NotEmpty()
            .Must(c => Enum.TryParse<DistributionCredentialCategory>(c, ignoreCase: true, out _))
            .WithMessage(
                "Category must be one of: "
                + "GeneralAgent, Broker, Proxy, CounterPresenter.");

        RuleFor(x => x.CardNumber).NotEmpty().MaximumLength(100);
        RuleFor(x => x.IssuingAuthority).NotEmpty().MaximumLength(200);

        RuleFor(x => x.ExpiresOn)
            .GreaterThan(x => x.IssuedOn)
            .WithMessage("A credential must expire after it was issued.");

        RuleFor(x => x.IndemnityInsurer).MaximumLength(200);
        RuleFor(x => x.IndemnityPolicyReference).MaximumLength(100);

        // Zero is allowed and means "recorded as none yet"; negative is a typo.
        RuleFor(x => x.TrainingHours)
            .GreaterThanOrEqualTo(0)
            .When(x => x.TrainingHours.HasValue);

        RuleFor(x => x.SupersedeReason).MaximumLength(500);
    }
}
