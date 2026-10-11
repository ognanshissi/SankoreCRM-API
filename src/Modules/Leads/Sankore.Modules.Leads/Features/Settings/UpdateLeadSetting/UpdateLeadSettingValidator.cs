namespace Sankore.Modules.Leads.Features.Settings.UpdateLeadSetting;

using FluentValidation;

/// <summary>
/// Shape only. Whether the key exists and whether the value parses as its declared type belong to
/// <c>ILeadSettings.SetAsync</c>, which owns the closed list — duplicating either here would give
/// two places to keep in step with the catalogue.
/// </summary>
internal sealed class UpdateLeadSettingValidator : AbstractValidator<UpdateLeadSettingCommand>
{
    public UpdateLeadSettingValidator()
    {
        RuleFor(x => x.Key).NotEmpty();

        // NotNull rather than NotEmpty: a parameter whose value is legitimately blank is possible
        // in a string setting, and the type check downstream is what refuses a blank int.
        RuleFor(x => x.Value).NotNull();
    }
}
