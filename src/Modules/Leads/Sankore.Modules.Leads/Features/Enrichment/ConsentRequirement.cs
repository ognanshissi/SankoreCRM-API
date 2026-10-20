namespace Sankore.Modules.Leads.Features.Enrichment;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Which <see cref="ConsentType"/> a required <c>CONSENT_*</c> code stands for. The Kernel holds the
/// codes (M12 stores them, M13 evaluates them) and cannot reference this module's enum, so the
/// mapping lives here, next to the calculator that reads it.
/// </summary>
internal static class ConsentRequirement
{
    /// <summary>Null for a <c>CONSENT_*</c> code this module does not map — evaluated UNSATISFIED.</summary>
    internal static ConsentType? TypeOf(string code) => code.Trim().ToUpperInvariant() switch
    {
        PersonFieldCode.ConsentDataProcessing    => ConsentType.DataProcessing,
        PersonFieldCode.ConsentCreditBureau      => ConsentType.CreditBureauCheck,
        PersonFieldCode.ConsentThirdPartySharing => ConsentType.ThirdPartySharing,
        _                                        => null,
    };
}
