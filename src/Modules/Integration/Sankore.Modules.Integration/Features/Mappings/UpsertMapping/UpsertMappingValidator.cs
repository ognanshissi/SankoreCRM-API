namespace Sankore.Modules.Integration.Features.Mappings.UpsertMapping;

using FluentValidation;

/// <summary>
/// Lengths mirror the column widths of <c>integration_mapping</c>: refusing here gives the caller
/// a 422 naming the field, where letting it through gives a truncation or a database error.
/// </summary>
internal sealed class UpsertMappingValidator : AbstractValidator<UpsertMappingCommand>
{
    public UpsertMappingValidator()
    {
        RuleFor(c => c.ConnectionId).NotEmpty();

        RuleFor(c => c.CrmCode)
            .NotEmpty().WithMessage("A CRM code is required.")
            .MaximumLength(100);

        RuleFor(c => c.ExternalCode)
            .NotEmpty().WithMessage(
                "An external code is required. To remove a translation, delete the mapping — a "
                + "blank external code would send nothing and look configured.")
            .MaximumLength(100);

        RuleFor(c => c.Label).MaximumLength(200);
    }
}
