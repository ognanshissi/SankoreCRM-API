namespace Sankore.Modules.Integration.Domain;

/// <summary>
/// Lifecycle of a queued write (INT-05). The transition table lives in
/// <see cref="IntegrationCommand"/> and is the only thing that may move a command between these.
/// </summary>
public enum CommandStatus
{
    /// <summary>Created, or replayed from <see cref="Rejected"/>. Waiting for the dispatcher.</summary>
    Pending,

    /// <summary>Claimed by a job. Entered under an optimistic lock on the status itself.</summary>
    Sending,

    /// <summary>Added to an outbound file; closed later by an acknowledgement (INT-24/25).</summary>
    Batched,

    /// <summary>Transient failure, attempts left. Resumes at <c>NextAttemptAt</c>.</summary>
    RetryScheduled,

    /// <summary>Functional or technical failure, or the attempt budget is spent.</summary>
    Rejected,

    /// <summary>Confirmed by the external system. Final.</summary>
    Succeeded,

    /// <summary>Cancelled by a human. Final.</summary>
    Cancelled
}

/// <summary>Direction of a batch file, from SANKORE's point of view.</summary>
public enum BatchDirection
{
    /// <summary>Written by us, deposited for the external system.</summary>
    Out,

    /// <summary>Produced by the external system: acknowledgements and extractions.</summary>
    In
}

/// <summary>
/// The code families that need translating between the CRM and an external system (INT-04).
/// Exactly the eight domains of the specification.
/// </summary>
public enum MappingDomain
{
    IdDocType,
    Product,
    Agency,
    Country,
    Gender,
    MaritalStatus,
    Profession,
    Sector
}

/// <summary>What a daily reconciliation can find (INT-34).</summary>
public enum GapType
{
    /// <summary>The CRM holds a reference the external system does not know.</summary>
    MissingInExternal,

    /// <summary>The external system holds a record the CRM has no reference for.</summary>
    MissingInCrm,

    /// <summary>Both know the customer and disagree on the KYC tier.</summary>
    KycMismatch,

    /// <summary>Both know the customer and disagree on whether it is active.</summary>
    StatusMismatch,

    /// <summary>
    /// The CRM holds a beneficiary clause the back office was never sent (BNF-B-17).
    ///
    /// <para>
    /// Detected from the CRM's clause version against the latest version a <c>SubscribePolicy</c>
    /// command actually succeeded with — both of which this module can see, so unlike a true
    /// clause comparison it needs nothing from the insurer. An avenant recorded while the insurer
    /// was unreachable, or one whose command was rejected and never re-sent, lands here.
    /// </para>
    ///
    /// <para>
    /// It is NOT "the two sides' clauses differ". Nothing on the insurer side records which
    /// version it holds, so that comparison has no input in this deployment — see
    /// <c>ReconciliationScope</c>, which says so rather than reporting a figure it cannot measure.
    /// </para>
    /// </summary>
    DesignationOutOfSync
}

/// <summary>
/// Where a gap stands. Stored rather than derived from a nullable timestamp so the index
/// <c>ix (tenant_id, resolution)</c> of the specification can serve "what is still open".
/// </summary>
public enum GapResolution
{
    /// <summary>Seen today and still true.</summary>
    Open,

    /// <summary>A human acted on it.</summary>
    Resolved,

    /// <summary>Gone on its own: the next run no longer found it.</summary>
    Closed
}

/// <summary>
/// The data streams a connection can synchronise (INT-20). The first four are core banking, the
/// next two insurance, the last telephony — one enum, because the cursor table and the
/// orchestrator are shared.
/// </summary>
public enum SyncStream
{
    Customers,
    Accounts,
    Transactions,
    Loans,
    Policies,
    Claims,

    /// <summary>
    /// Calls read back from the telephony provider (TEL-B-14). Its purpose is not to be the
    /// primary source — webhooks are — but to catch the calls whose event was lost, which is why
    /// it is swept far more often than the other streams.
    /// </summary>
    Calls
}

/// <summary>
/// What a command asks the external system to do. A string column in the end, but an enum here
/// so the dispatcher's switch is exhaustive and a new operation cannot be forgotten.
/// </summary>
public enum CommandType
{
    CreateCustomer,
    UpdateCustomer,
    SetKycLevel,
    OpenAccount,
    SubmitLoanApplication,
    DebitAccount,
    ReverseDebit,
    SubscribePolicy,
    CancelPolicy,
    DeclareClaim,

    /// <summary>
    /// A <i>pièce de sinistre</i> the insurer asked for after the declaration (ASS-09).
    ///
    /// <para>
    /// A command type of its own because <c>IInsuranceClaimPort.AddDocumentAsync</c> takes an
    /// <c>IdempotencyKey</c> and is therefore a write — it has to be queued, retried and
    /// reconciled like every other. It deliberately rides on
    /// <c>IntegrationCapability.DeclareClaim</c> rather than gaining a capability of its own:
    /// <c>OrassOperations.AddClaimDocument</c> already states the reason, which is that the
    /// follow-up an insurer asks for after a declaration belongs to the declaration. An insurer
    /// that takes declarations takes their complements, and a second capability would be a second
    /// switch an administrator could leave off while the first was on.
    /// </para>
    /// </summary>
    AddClaimDocument
}

/// <summary>
/// CRM entity types this module holds references for. A STRING in the database
/// (<c>entity_type</c>, max 60) following the established convention of this repo — see the
/// plan document for why there is no shared enum — but the known values are named here so the
/// module's own code never spells them twice.
/// </summary>
public static class IntegrationEntityTypes
{
    public const string Customer = "Customer";
    public const string Account = "Account";
    public const string Loan = "Loan";
    public const string Policy = "Policy";
    public const string Claim = "Claim";
}
