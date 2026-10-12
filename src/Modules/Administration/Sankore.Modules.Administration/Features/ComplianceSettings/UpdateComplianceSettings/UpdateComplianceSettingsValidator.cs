namespace Sankore.Modules.Administration.Features.ComplianceSettings.UpdateComplianceSettings;

using FluentValidation;
using Sankore.Modules.Administration.Domain;

internal sealed class UpdateComplianceSettingsValidator
    : AbstractValidator<UpdateComplianceSettingsCommand>
{
    public UpdateComplianceSettingsValidator()
    {
        // Parseability only. Whether the pair is COMPLETE when enabling is the aggregate's call
        // (ConfigureFatca), so the rule lives in one place and holds for every caller — a seeder
        // or a future import included, neither of which goes through this validator.
        RuleFor(c => c.FatcaRequiredAt)
            .NotEmpty()
            .Must(v => Enum.TryParse<FatcaRequiredAt>(v, ignoreCase: false, out _))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Enum.GetNames<FatcaRequiredAt>())}.");

        RuleFor(c => c.FatcaRegime)
            .Must(v => Enum.TryParse<FatcaRegime>(v, ignoreCase: false, out _))
            .WithMessage($"'{{PropertyName}}' must be one of: {string.Join(", ", Enum.GetNames<FatcaRegime>())}.")
            .When(c => !string.IsNullOrWhiteSpace(c.FatcaRegime));

        // Shape here as well as in the aggregate, and deliberately: a malformed GIIN is a typo in
        // a form and deserves the field-level 400 a validator produces, not a bare error code.
        RuleFor(c => c.FatcaGiin)
            .Must(Domain.ComplianceSettings.GiinIsWellFormed)
            .WithMessage("'{PropertyName}' must match the GIIN format XXXXXX.XXXXX.XX.XXX.")
            .When(c => !string.IsNullOrWhiteSpace(c.FatcaGiin));

        // The window is validated whether or not vigilance is on, matching ConfigurePep: the
        // value stored while it is off is the one a re-enable will use, so accepting nonsense here
        // only defers the refusal to the moment somebody turns it back on.
        //
        // PepEnabled itself gets no rule — a bool has no invalid value, and the question of
        // whether it SHOULD be switchable is recorded on the domain property, not here.
        RuleFor(c => c.PepPostMandateMonths)
            .InclusiveBetween(0, 600)
            .WithMessage("'{PropertyName}' must be between 0 and 600 months.");
    }
}
