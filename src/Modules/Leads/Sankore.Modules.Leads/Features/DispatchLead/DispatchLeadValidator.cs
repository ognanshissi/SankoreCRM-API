using FluentValidation;
using Microsoft.Extensions.Localization;
using Sankore.Modules.Leads.Resources;

namespace Sankore.Modules.Leads.Features.DispatchLead;

/// <summary>
/// Runs automatically before DispatchLeadHandler via ValidationBehavior
/// (registered once, globally, in the Bootstrapper). If this fails, the
/// handler body never executes.
/// </summary>
public sealed class DispatchLeadValidator : AbstractValidator<DispatchLeadCommand>
{
    public DispatchLeadValidator(IStringLocalizer<LeadsErrors> localizer)
    {
        RuleFor(x => x.LeadId)
            .NotEmpty()
            .WithMessage(_ => localizer["Lead.LeadId.Required"]);

        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage(_ => localizer["Lead.TenantId.Required"]);

        RuleFor(x => x.Strategy)
            .IsInEnum()
            .WithMessage(_ => localizer["Lead.Strategy.Unknown"]);
    }
}
