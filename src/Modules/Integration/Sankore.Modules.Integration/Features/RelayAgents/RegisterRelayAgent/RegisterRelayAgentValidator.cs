namespace Sankore.Modules.Integration.Features.RelayAgents.RegisterRelayAgent;

using FluentValidation;

/// <summary>
/// Shape only, and one field to check.
///
/// <para>
/// 120 characters is the column's width. Refused here rather than at the database, because the
/// aggregate's <c>DomainException</c> and a truncation are both worse answers than a 422 naming
/// the field: this endpoint mints a credential, and a caller that gets an unexplained 500 will
/// retry — leaving an armed token behind each time if the write had in fact succeeded.
/// </para>
/// </summary>
internal sealed class RegisterRelayAgentValidator : AbstractValidator<RegisterRelayAgentCommand>
{
    public RegisterRelayAgentValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .OverridePropertyName("name")
            .WithMessage("An agent name is required.")
            .MaximumLength(120)
            .OverridePropertyName("name")
            .WithMessage("An agent name cannot exceed 120 characters.");
    }
}
