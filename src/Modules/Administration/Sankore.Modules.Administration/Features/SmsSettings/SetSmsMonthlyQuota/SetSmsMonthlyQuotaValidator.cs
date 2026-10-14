namespace Sankore.Modules.Administration.Features.SmsSettings.SetSmsMonthlyQuota;

using FluentValidation;

internal sealed class SetSmsMonthlyQuotaValidator : AbstractValidator<SetSmsMonthlyQuotaCommand>
{
    public SetSmsMonthlyQuotaValidator()
    {
        // Zero is refused rather than read as "none": it would silently stop every message of the
        // tenant, including a customer's one-time code, and an operator who means that says it by
        // disabling the channel. Null is the way to say "no ceiling".
        RuleFor(x => x.MonthlyQuotaLimit)
            .GreaterThan(0)
            .When(x => x.MonthlyQuotaLimit.HasValue)
            .WithMessage("Un plafond doit être strictement positif. Pour ne pas en fixer, "
                       + "laissez le champ vide ; pour couper le canal, désactivez-le.");
    }
}
