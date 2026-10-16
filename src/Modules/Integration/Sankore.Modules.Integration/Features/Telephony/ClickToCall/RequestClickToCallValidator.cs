namespace Sankore.Modules.Integration.Features.Telephony.ClickToCall;

using FluentValidation;
using Sankore.Modules.Integration.Domain;

internal sealed class RequestClickToCallValidator : AbstractValidator<RequestClickToCallCommand>
{
    public RequestClickToCallValidator()
    {
        // The closed list is in the domain and is checked here too, so a caller gets a 422 naming
        // the field rather than a refusal from three layers down. TelEntityTypes.IsKnown is the
        // single definition; this is not a second one.
        RuleFor(x => x.EntityType)
            .Must(TelEntityTypes.IsKnown)
            .OverridePropertyName("entityType")
            .WithMessage("entityType must be 'Lead' or 'Customer'.");

        RuleFor(x => x.EntityId)
            .NotEmpty()
            .OverridePropertyName("entityId");

        RuleFor(x => x.Slot).IsInEnum().OverridePropertyName("slot");
    }
}
