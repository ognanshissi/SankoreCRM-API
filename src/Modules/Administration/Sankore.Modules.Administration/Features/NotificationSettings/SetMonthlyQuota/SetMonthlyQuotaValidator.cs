using FluentValidation;

namespace Sankore.Modules.Administration.Features.NotificationSettings.SetMonthlyQuota;

internal sealed class SetMonthlyQuotaValidator : AbstractValidator<SetMonthlyQuotaCommand>
{
    public SetMonthlyQuotaValidator()
    {
        When(x => x.MonthlyQuotaLimit.HasValue, () =>
        {
            RuleFor(x => x.MonthlyQuotaLimit!.Value)
                .GreaterThan(0)
                .WithMessage("MonthlyQuotaLimit must be a positive integer or null (unlimited).");
        });
    }
}
