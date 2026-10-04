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

        // Naming an agent and naming a strategy are two different requests: a strategy ranks a
        // pool, an explicit agent skips the ranking entirely. Accepting both would mean silently
        // ignoring one of them, so refuse instead of guessing which the caller meant.
        RuleFor(x => x.Strategy)
            .Null()
            .When(x => x.AgentId.HasValue)
            .WithMessage("Strategy cannot be combined with an explicit AgentId.");

        // An override has to say why: the row is written with WasManualOverride = true and the
        // reason is the only thing that explains it afterwards.
        RuleFor(x => x.OverrideReason)
            .NotEmpty()
            .MaximumLength(500)
            .When(x => x.AgentId.HasValue)
            .WithMessage("OverrideReason is required when an explicit AgentId is given.");

        RuleFor(x => x.OverrideReason)
            .Empty()
            .When(x => !x.AgentId.HasValue)
            .WithMessage("OverrideReason only applies to an explicit AgentId.");
    }
}
