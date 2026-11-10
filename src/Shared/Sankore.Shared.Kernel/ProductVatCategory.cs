namespace Sankore.Shared.Kernel;

/// <summary>
/// Which of the institution's VAT rates a Standard product carries (quote lines, lot 1, 2026-11-10,
/// product owner). The RATES themselves are the institution's (<c>CompanyInfo.VatStandardRate</c>,
/// <c>VatReducedRate</c>), never coded here: they differ from one UEMOA country to another and move
/// with each finance law.
/// </summary>
public enum ProductVatCategory
{
    /// <summary>The institution's standard rate.</summary>
    Standard,
    /// <summary>The institution's reduced rate.</summary>
    Reduced,
    /// <summary>No VAT.</summary>
    Exempt,
}
