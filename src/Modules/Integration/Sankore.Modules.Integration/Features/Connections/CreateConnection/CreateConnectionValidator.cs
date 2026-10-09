namespace Sankore.Modules.Integration.Features.Connections.CreateConnection;

using FluentValidation;

/// <summary>
/// The row's own fields; the per-kind settings rules come from
/// <see cref="ConnectionSettingsValidator{TCommand}"/>.
/// </summary>
internal sealed class CreateConnectionValidator : ConnectionSettingsValidator<CreateConnectionCommand>
{
    public CreateConnectionValidator()
    {
        RuleFor(x => x.Family).IsInEnum().OverridePropertyName("family");
        RuleFor(x => x.Kind).IsInEnum().OverridePropertyName("kind");

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(120)
            .OverridePropertyName("name")
            .WithMessage("name is required and cannot exceed 120 characters.");

        // The settings object and the kind must agree. Refused here as well as in the aggregate
        // so the caller gets a 422 naming the field rather than a 500 from a DomainException: a
        // Temenos row carrying Amplitude settings would resolve an adapter that cannot read its
        // own configuration.
        RuleFor(x => x.Settings)
            .Must((cmd, settings) => settings!.ExpectedKind == cmd.Kind)
            .When(x => x.Settings is not null)
            .OverridePropertyName("settings")
            .WithMessage(cmd =>
                $"settings of kind {cmd.Settings!.ExpectedKind} cannot be stored on a {cmd.Kind} connection.");

        // There is no rule about the relay agent: the id is not in the command at all. A Relay
        // connection is created without one and INT-27's enrolment flow attaches it, which is the
        // only place that can guarantee the agent belongs to this tenant.
    }
}
