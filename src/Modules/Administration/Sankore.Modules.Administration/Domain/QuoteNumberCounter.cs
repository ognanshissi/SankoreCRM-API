namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// The last quote number given out in a year (quote lines, lot 2, 2026-11-10): DEV-2026-000001,
/// then 000002… starting again at 1 each January. One row per tenant and year, incremented
/// atomically by <c>QuoteNumbering</c>.
/// </summary>
/// <remarks>
/// Kept on the tenant of the DATA, not of the team: a « Mode test » twin numbers its own trial quotes
/// and never takes a number production will need. A number is never given back — a quote abandoned
/// after its number was taken leaves a gap, which is acceptable for a quote (it is not an invoice:
/// the invoice and its unbroken sequence stay in the institution's invoicing software).
/// </remarks>
public sealed class QuoteNumberCounter
{
    public Guid TenantId { get; private set; }
    public int Year { get; private set; }
    public long LastNumber { get; private set; }

    private QuoteNumberCounter() { }

    public static QuoteNumberCounter Start(Guid tenantId, int year) =>
        new() { TenantId = tenantId, Year = year, LastNumber = 0 };

    public long Next() => ++LastNumber;
}
