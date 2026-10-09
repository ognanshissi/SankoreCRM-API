namespace Sankore.Modules.Integration.Adapters.PerfectVision;

using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The missing document, named once (INT-28, last acceptance criterion: « Prérequis :
/// spécification d'interface Perfect Vision obtenue (question ouverte) »).
///
/// <para>
/// A type rather than a comment, and one place rather than ten literals, because this string is
/// the only thing an operator will ever see from this adapter. It reaches them through the
/// <c>Detail</c> of an <see cref="IntegrationResult"/> — a rejected command, a failed health
/// check, a call-journal row — and it has to tell them the truth: nothing is broken, a supplier
/// deliverable has not arrived, and no change in this repository can lift it.
/// </para>
///
/// <para>
/// It is deliberately not phrased "not implemented". That phrasing sends an operator to raise a
/// defect, and a developer to look for the half-written method that is supposed to exist. Both
/// waste their time: the adapter is as complete as it can be without the vendor.
/// </para>
/// </summary>
public static class PerfectVisionSpecification
{
    /// <summary>Where the block is recorded, with the three other adapters in the same state.</summary>
    public const string PlanReference = "docs/integration-module-plan.md §6";

    /// <summary>
    /// What is missing, in one sentence an operator can forward to whoever owns the supplier
    /// relationship. Never a field name, never a record layout: inventing either is the failure
    /// mode this whole type exists to prevent.
    /// </summary>
    public const string MissingDocument =
        "the Perfect Vision file layout has not been obtained";

    /// <summary>
    /// The three questions to settle with the vendor, in the order they unblock work.
    ///
    /// <para>
    /// Ordered, and not a set: question 1 decides whether a file can be written at all, question 2
    /// decides whether a written file can ever be closed (a batch command stays <c>Batched</c>
    /// until an acknowledgement closes it — INT-24/INT-25), and question 3 decides only whether
    /// one read is live or comes from the snapshot. Answering 3 first unblocks nothing.
    /// </para>
    ///
    /// <para>
    /// Exposed rather than left in a comment so that the registration, the tests and an operator
    /// screen can all print the same list. A procurement conversation that gets three different
    /// versions of "what we need" gets none of them.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> OpenQuestions =
    [
        "1. The outbound file layout and its encoding: which record types exist, which fields each "
        + "carries, in what order, fixed-width or delimited, and in which code page. "
        + "BatchCapableSettings already offers FileEncoding and FieldSeparator, so a delimited "
        + "file in a non-UTF-8 code page is configurable — but not guessable.",

        "2. The acknowledgement format: what Perfect Vision writes back, under what file name, in "
        + "which directory, and how one acknowledgement line identifies the record it answers. "
        + "Without it a command can be sent and never closed, which is worse than refusing to "
        + "send it.",

        "3. Whether the read-only balance view exists in this installation, and if so its exact "
        + "columns and their types. PerfectVisionSettings.BalanceViewName already carries the "
        + "name; the columns are what a query needs and what no document here defines.",
    ];

    /// <summary>
    /// The <c>Detail</c> every refusal carries. Names the operation so a call-journal row is
    /// readable on its own, then the missing document, then where the block is recorded.
    /// </summary>
    /// <param name="operation">
    /// The logical operation, from <see cref="PerfectVisionOperations"/> — a constant and not a
    /// literal, so that the word in this message is the same word the capability matrix uses.
    /// </param>
    public static string RefusalDetail(string operation)
        => $"Perfect Vision cannot serve {operation}: {MissingDocument}. "
           + $"This is a supplier dependency and not a defect — see {PlanReference}. "
           + "Writing a plausible mapping without the document would produce an adapter that "
           + "reports writes the core banking system never accepted.";

    /// <summary>
    /// The same statement for the health check, which answers an operator staring at an
    /// activation screen rather than a queued command.
    /// </summary>
    public static string HealthDetail()
        => $"Perfect Vision is not operable: {MissingDocument}, so no file can be produced and no "
           + $"acknowledgement can be read. See {PlanReference}. The connection may be configured "
           + "and will stay inactive until the specification arrives.";
}

/// <summary>
/// The logical operation names, as constants.
///
/// <para>
/// Same reason as <c>TemenosOperations</c>: the word appears in a refusal detail and in the
/// capability matrix, and a typo would give an administrator reading the matrix and an operator
/// reading a rejection two different vocabularies for one operation. They match the
/// <see cref="IntegrationCapability"/> names exactly.
/// </para>
/// </summary>
public static class PerfectVisionOperations
{
    public const string CheckHealth = "CheckHealth";
    public const string CreateCustomer = "CreateCustomer";
    public const string UpdateCustomer = "UpdateCustomer";
    public const string SetKycLevel = "SetKycLevel";
    public const string OpenAccount = "OpenAccount";
    public const string ReadAccounts = "ReadAccounts";
    public const string ReadBalance = "ReadBalance";
    public const string SubmitLoanApplication = "SubmitLoanApplication";
    public const string ReadLoans = "ReadLoans";
    public const string DebitAccount = "DebitAccount";
    public const string ReverseDebit = "ReverseDebit";
}
