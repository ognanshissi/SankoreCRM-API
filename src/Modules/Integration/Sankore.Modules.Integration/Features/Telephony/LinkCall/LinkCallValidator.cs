namespace Sankore.Modules.Integration.Features.Telephony.LinkCall;

using FluentValidation;
using Sankore.Modules.Integration.Domain;

internal sealed class LinkCallValidator : AbstractValidator<LinkCallCommand>
{
    public LinkCallValidator()
    {
        RuleFor(x => x.CallId).NotEmpty().OverridePropertyName("callId");

        RuleFor(x => x.EntityType)
            .Must(TelEntityTypes.IsKnown)
            .OverridePropertyName("entityType")
            .WithMessage("entityType must be 'Lead' or 'Customer'.");

        RuleFor(x => x.EntityId).NotEmpty().OverridePropertyName("entityId");

        RuleFor(x => x.Reason)
            .NotEmpty()
            .MaximumLength(500)
            .OverridePropertyName("reason")
            .WithMessage("reason is required and cannot exceed 500 characters.");
    }
}
