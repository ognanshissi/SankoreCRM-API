namespace Sankore.Modules.Leads.Features.DispatchLead;

using FluentValidation;

/// <summary>
/// Runs automatically before DispatchLeadHandler via ValidationBehavior
/// (registered once, globally, in the Bootstrapper). If this fails, the
/// handler body never executes.
/// </summary>
public sealed class DispatchLeadValidator : AbstractValidator<DispatchLeadCommand>
{
    public DispatchLeadValidator()
    {
        RuleFor(x => x.LeadId)
            .NotEmpty()
            .WithMessage("LeadId is required.");

        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage("TenantId is required for multi-tenant isolation.");

        RuleFor(x => x.Strategy)
            .IsInEnum()
            .WithMessage("Unknown dispatching strategy.");
    }
}
