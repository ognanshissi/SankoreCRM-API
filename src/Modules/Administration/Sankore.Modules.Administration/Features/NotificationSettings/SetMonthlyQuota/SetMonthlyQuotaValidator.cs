using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Administration.Resources;

namespace Sankore.Modules.Administration.Features.NotificationSettings.SetMonthlyQuota;

internal sealed class SetMonthlyQuotaValidator : AbstractValidator<SetMonthlyQuotaCommand>
{
    public SetMonthlyQuotaValidator(IStringLocalizer<AdministrationErrors> localizer)
    {
        When(x => x.MonthlyQuotaLimit.HasValue, () =>
        {
            RuleFor(x => x.MonthlyQuotaLimit!.Value)
                .GreaterThan(0)
                .WithMessage(_ => localizer["NotificationSettings.Quota.Invalid"]);
        });
    }
}
