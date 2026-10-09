namespace Sankore.Modules.Integration.Features.Connections.UpdateConnection;

using FluentValidation;

internal sealed class UpdateConnectionValidator : ConnectionSettingsValidator<UpdateConnectionCommand>
{
    public UpdateConnectionValidator()
    {
        RuleFor(x => x.ConnectionId).NotEmpty().OverridePropertyName("connectionId");

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(120)
            .OverridePropertyName("name")
            .WithMessage("name is required and cannot exceed 120 characters.");

        // The kind the settings belong to is NOT checked here: it is the stored row's kind, which
        // only the handler can see. It answers INTEGRATION_SETTINGS_INVALID.

        RuleFor(x => x.ExpectedVersion)
            .Must((cmd, _) => cmd.ExpectedVersion is not null || cmd.ExpectedUpdatedAt is not null)
            .OverridePropertyName("expectedVersion")
            .WithMessage("expectedVersion or expectedUpdatedAt is required: an update without a "
                         + "concurrency token silently overwrites a concurrent one.");

        // No rule about the relay agent: the id is not in the command at all, and the handler
        // carries the stored one through. INT-27's enrolment flow is the only writer.
    }
}
