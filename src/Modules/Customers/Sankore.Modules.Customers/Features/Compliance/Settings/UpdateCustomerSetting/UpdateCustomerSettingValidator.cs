namespace Sankore.Modules.Customers.Features.Compliance.Settings.UpdateCustomerSetting;

using FluentValidation;
using Sankore.Modules.Customers.Features.Compliance.Shared;

/// <summary>
/// Shape and type checks. The unknown-key case is deliberately NOT handled here: the caller
/// must see the contract code <c>SETTING_UNKNOWN</c> (404) rather than a generic 400, so the
/// handler owns that decision and the validator only validates known keys.
/// </summary>
public sealed class UpdateCustomerSettingValidator : AbstractValidator<UpdateCustomerSettingCommand>
{
    public UpdateCustomerSettingValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(80);

        // 4000 is the column width; the JSON settings (segment rules, loyalty weights) are the
        // only values that come anywhere near it.
        RuleFor(x => x.Value).NotNull().MaximumLength(4000);

        // One rule covers every declared type (int / bool / decimal / json / string) plus the
        // per-key floors, so a new key in CustomerSettingKeys.Defaults is validated the day it
        // is added without touching this class.
        RuleFor(x => x)
            .Must(cmd => CustomerSettingValueValidation.Validate(cmd.Key, cmd.Value) is null)
            .WithName(nameof(UpdateCustomerSettingCommand.Value))
            .WithMessage(cmd => CustomerSettingValueValidation.Validate(cmd.Key, cmd.Value)
                                ?? "Invalid setting value.")
            .When(cmd => CustomerSettingValueValidation.IsKnownKey(cmd.Key));
    }
}
