namespace Sankore.Modules.Integration.Adapters.Sab;

using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The missing document, named once (INT-32, last acceptance criterion: « Prérequis : catalogue
/// d'API Open SAB, à négocier avec SBS en même temps qu'Amplitude »).
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
/// waste their time: the adapter is as complete as it can be without SBS. What the message must
/// do instead is name a counterparty, so that whoever reads it forwards it to whoever owns the
/// SBS relationship — the same conversation that has to happen for Amplitude (INT-31), which is
/// why the prerequisite names the two together.
/// </para>
///
/// <para>
/// <b>What this type is NOT allowed to contain</b>, now or ever: a path, a verb, a header name, a
/// field name, an entity code. An invented identifier in a message is how an invented identifier
/// reaches a mapper — somebody reads the error, takes the name for documentation, and the guess
/// becomes the contract. A test pins the absence.
/// </para>
/// </summary>
public static class SabSpecification
{
    /// <summary>Where the block is recorded, with the three other adapters in the same state.</summary>
    public const string PlanReference = "docs/integration-module-plan.md §6";

    /// <summary>
    /// What is missing, in one sentence an operator can forward to whoever owns the SBS
    /// relationship. Never an endpoint, never a field name: inventing either is the failure mode
    /// this whole type exists to prevent.
    /// </summary>
    public const string MissingDocument =
        "the Open SAB API catalogue has not been obtained";

    /// <summary>
    /// The four questions to settle with SBS (or with an IMF that already holds the catalogue),
    /// in the order they unblock work.
    ///
    /// <para>
    /// Ordered, and not a set. Question 1 decides whether any call can be <i>built</i>; question 2
    /// decides whether a built call may be <i>sent</i> — it is the one that carries the
    /// cross-institution risk, and its second half decides whether a mis-scoped call is
    /// detectable at all; question 3 only refines which capabilities stay live and which fall back
    /// to the INT-21 snapshot; question 4 is what closes criterion 3, and it unblocks nothing on
    /// its own — contract tests cannot run against a client question 1 has not made writable.
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
        "1. The catalogue itself: which Open SAB services this installation exposes, their paths "
        + "and verbs, and for each operation the request and response field names and their types "
        + "— including which of them the IMF's own licence covers. SabSettings already carries the "
        + "base URL, so the address is configurable; the service list and the field names are what "
        + "no document here defines, and the whole point of this chantier is that they are not "
        + "guessable.",

        "2. How the API key and the Entity travel on a request: the name and the placement of each "
        + "(header, path segment, query string, body), and — the half that matters most — whether "
        + "one API key is bound to a single entity or addresses the whole installation. A key bound "
        + "to its entity means the far end refuses a call naming another institution, and a "
        + "mis-scoped call is caught by SAB itself. An installation-wide key means the Entity we "
        + "send is the ONLY thing standing between two institutions' customers on a network such "
        + "as CIF, and the answer to this question is what decides whether the entity can ever be "
        + "verified rather than merely required (see SabEntityScope).",

        "3. Which reads Open SAB answers — accounts, balance, transaction history, monthly "
        + "aggregates, the KYC tier — and how it paginates them. This decides which capabilities "
        + "stay RealTime and which must be served from the INT-21 snapshot, and in particular "
        + "whether ICbsKycLevelPort may be claimed at all: INT-21's divergence check is only "
        + "meaningful if the tier can actually be read back.",

        "4. A test environment, from the IMF or from SBS, carrying AT LEAST TWO entities on one "
        + "installation. Criterion 3 asks for green contract tests, and a single-entity sandbox "
        + "cannot green the assertion that matters: that a call scoped to entity A never sees "
        + "entity B's customers. One entity would let the scoping be wrong and the suite stay "
        + "green.",
    ];

    /// <summary>
    /// The <c>Detail</c> every catalogue refusal carries. Names the operation so a call-journal
    /// row is readable on its own, then the missing document, then where the block is recorded.
    /// </summary>
    /// <param name="operation">
    /// The logical operation, from <see cref="SabOperations"/> — a constant and not a literal, so
    /// that the word in this message is the same word the capability matrix uses.
    /// </param>
    public static string RefusalDetail(string operation)
        => $"SAB AT cannot serve {operation}: {MissingDocument}. "
           + $"This is a supplier dependency on SBS and not a defect — see {PlanReference}. "
           + "Writing a plausible mapping without the catalogue would produce an adapter that "
           + "reports writes the core banking system never accepted.";

    /// <summary>
    /// The same statement for the health check, which answers an operator staring at an
    /// activation screen rather than a queued command.
    /// </summary>
    public static string HealthDetail()
        => $"SAB AT is not operable: {MissingDocument}, so no Open SAB call can be built. "
           + $"See {PlanReference}. The connection may be configured and will stay inactive until "
           + "the catalogue arrives.";
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
public static class SabOperations
{
    public const string CheckHealth = "CheckHealth";
    public const string CreateCustomer = "CreateCustomer";
    public const string UpdateCustomer = "UpdateCustomer";
    public const string SetKycLevel = "SetKycLevel";
    public const string OpenAccount = "OpenAccount";
    public const string ReadAccounts = "ReadAccounts";
    public const string ReadBalance = "ReadBalance";
    public const string ReadTransactions = "ReadTransactions";
    public const string ReadMonthlyFlow = "ReadMonthlyFlow";
    public const string SubmitLoanApplication = "SubmitLoanApplication";
    public const string ReadLoans = "ReadLoans";
    public const string DebitAccount = "DebitAccount";
    public const string ReverseDebit = "ReverseDebit";
}
