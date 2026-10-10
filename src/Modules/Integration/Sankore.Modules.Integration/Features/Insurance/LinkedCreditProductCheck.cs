namespace Sankore.Modules.Integration.Features.Insurance;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Verifies the credit product an insurance product is attached to (ASS-03, criterion 3 —
/// <i>assurance emprunteur</i>).
///
/// <para>
/// <b>Checked at write time, by code, across a module boundary and never a foreign key.</b> M12's
/// products live in the <c>administration</c> schema; this module reaches them only through
/// <c>IAdministrationModule.GetProductCategoryAsync</c>, the contract M13 already uses for the
/// same lookup. The code is validated when it is written and every reader afterwards degrades to
/// "unlinked" rather than assuming it still resolves — the rule this repo applies to
/// <c>LeadAssignment.RuleId</c> and <c>IntegrationConnection.RelayAgentId</c>.
/// </para>
///
/// <para>
/// It must resolve AND resolve to a loan. A borrower's insurance attached to a savings product is
/// not a typo an agent would catch: the subscription screen would offer life cover alongside a
/// passbook, and the eligibility rule « détention d'un crédit » would then be checked against a
/// product that can never satisfy it.
/// </para>
/// </summary>
internal sealed class LinkedCreditProductCheck(
    IAdministrationModule administration,
    ITenantContext tenant,
    ILogger<LinkedCreditProductCheck> logger)
{
    /// <summary>The category M12 reports for a loan product. Compared case-insensitively.</summary>
    private const string LoanCategory = "Loan";

    /// <summary>
    /// Success for a blank code — most products link to no credit — and a named failure for one
    /// that does not resolve or is not a loan.
    /// </summary>
    internal async Task<Result> VerifyAsync(string? linkedCreditProductCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(linkedCreditProductCode)) return Result.Ok();

        var code = linkedCreditProductCode.Trim();

        var category = await administration.GetProductCategoryAsync(
            tenant.CurrentTenantId, code, ct);

        if (category is null)
        {
            logger.LogInformation(
                "Insurance product refused: credit product {Code} is unknown to the tenant's catalogue",
                code);

            return Result.Fail(
                $"{IntegrationErrors.InsuranceLinkedCreditProductInvalid}: no product '{code}' "
                + "exists in this tenant's catalogue.");
        }

        if (!string.Equals(category, LoanCategory, StringComparison.OrdinalIgnoreCase))
            return Result.Fail(
                $"{IntegrationErrors.InsuranceLinkedCreditProductInvalid}: product '{code}' is a "
                + $"{category} product, and a borrower's insurance can only be attached to a loan.");

        return Result.Ok();
    }
}
