namespace Sankore.Modules.Integration.Features.Insurance.Statements;

internal static class StatementErrors
{
    public const string NotFound = "INSURANCE_STATEMENT_NOT_FOUND";

    /// <summary>
    /// A period that is not a month. Its own code rather than a validation message, because the
    /// same string reaches both a query parameter and the aggregate, which refuses it too.
    /// </summary>
    public const string PeriodInvalid = "INSURANCE_STATEMENT_PERIOD_INVALID";

    /// <summary>
    /// Finalising a statement that is already closed. A conflict and not a 400: the request is
    /// well-formed and the state refuses it.
    /// </summary>
    public const string AlreadyClosed = "INSURANCE_STATEMENT_ALREADY_CLOSED";
}
