namespace Sankore.Modules.Kyc.Features.Settings.UpdateKycSetting;

using FluentValidation;
using Sankore.Modules.Kyc.Domain;

/// <summary>
/// Shape and range. The unknown-key case is deliberately NOT handled here: the caller must get the
/// contract code <c>KYC_SETTING_UNKNOWN</c> with a 404, not a generic 400, so the handler owns that
/// decision and this validator only speaks about keys that exist.
///
/// <para>
/// The range rule exists so an administrator reads "le seuil d'alerte est un pourcentage entre 1 et
/// 100" instead of a bare error code. The same check runs again inside
/// <c>KycSettingsService.SetAsync</c>, which is the single write path — a job or another module can
/// dispatch this command without passing through the HTTP pipeline, and must not be able to store a
/// zero-day flow window.
/// </para>
/// </summary>
internal sealed class UpdateKycSettingValidator : AbstractValidator<UpdateKycSettingCommand>
{
    public UpdateKycSettingValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(100);

        // 2000 is the column width.
        RuleFor(x => x.Value).NotNull().MaximumLength(2000);

        // One rule covers every declared key, so a parameter added to KycSettingKeys.Defaults is
        // validated the day it is declared without touching this class.
        RuleFor(x => x)
            .Must(cmd => KycSettingRanges.Validate(cmd.Key, cmd.Value) is null)
            .WithName(nameof(UpdateKycSettingCommand.Value))
            .WithMessage(cmd => KycSettingRanges.Validate(cmd.Key, cmd.Value) ?? "Valeur invalide.")
            .When(cmd => KycSettingKeys.Find(cmd.Key) is not null);
    }
}
