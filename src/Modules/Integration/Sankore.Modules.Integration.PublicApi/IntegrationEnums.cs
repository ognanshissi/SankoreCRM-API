namespace Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Which family of back-office a connection talks to. The socle (outbox, dispatcher, batch,
/// relay agent, reconciliation) is shared by both; only the ports differ.
///
/// <para>
/// A tenant has at most ONE active <see cref="CoreBanking"/> connection and may have several
/// active <see cref="Insurance"/> ones — the partial unique index on
/// <c>integration_connection</c> enforces exactly that asymmetry (ASS-01).
/// </para>
/// </summary>
public enum IntegrationFamily
{
    CoreBanking,
    Insurance
}

/// <summary>
/// The product on the other end. One adapter per value, resolved through keyed DI.
///
/// <para>
/// <see cref="Fake"/> is selectable in Development only: it answers plausible successes with no
/// back-office at all, and a deployment that reached it by accident would report writes that
/// never happened.
/// </para>
/// </summary>
public enum IntegrationKind
{
    Temenos,
    Amplitude,
    Sab,
    PerfectVision,
    Orass,
    Fake
}

/// <summary>
/// How SANKORE reaches the system.
///
/// <list type="bullet">
/// <item><see cref="Api"/> — direct HTTP from this process.</item>
/// <item><see cref="Batch"/> — files exchanged over SFTP; a command is <c>Batched</c> rather than
///   sent, and closed later by an acknowledgement (INT-24/INT-25).</item>
/// <item><see cref="Relay"/> — through the on-premise agent, which holds the only outbound
///   connection; SANKORE opens no port into the IMF's network (INT-26).</item>
/// </list>
/// </summary>
public enum IntegrationMode
{
    Api,
    Batch,
    Relay
}

/// <summary>
/// Why a call failed — and therefore what the platform does next. The three families are the
/// whole point of <see cref="IntegrationResult"/>: a retry decided from an HTTP status code
/// scattered across adapters is a retry that eventually hammers a system that refused on the
/// merits.
/// </summary>
public enum ErrorFamily
{
    /// <summary>Timeout, 503, maintenance window, relay unreachable — retried with backoff.</summary>
    Transient,

    /// <summary>
    /// Duplicate, document refused, unknown product — the external system answered, and said no.
    /// Never retried: it goes to the rejection queue for a human.
    /// </summary>
    Functional,

    /// <summary>
    /// Missing mapping, invalid payload, authentication refused — OUR configuration is wrong.
    /// Never retried, and the administrator is alerted: retrying cannot fix it.
    /// </summary>
    Technical
}

/// <summary>Whether a capability is served live or only through the batch cycle.</summary>
public enum CapabilityMode
{
    RealTime,
    Batch
}

/// <summary>
/// What an adapter can actually do. Declared per capability rather than inferred from
/// <see cref="IntegrationKind"/>: the same product answers differently depending on the version
/// installed at the IMF (INT-31 switches on <c>AmplitudeSettings.AmplitudeVersion</c>).
/// </summary>
public enum IntegrationCapability
{
    CreateCustomer,
    UpdateCustomer,
    SetKycLevel,

    /// <summary>
    /// Reading the tier back from the external system. Separate from <see cref="SetKycLevel"/>
    /// because the two are genuinely independent: Temenos Transact lets a party's KYC status be
    /// written and read, while a batch-file CBS accepts the write and exposes no query at all.
    /// Without this capability INT-21 can only infer the tier from our own acknowledged writes,
    /// which cannot see a tier an officer changed inside the CBS.
    /// </summary>
    ReadKycLevel,
    OpenAccount,
    ReadAccounts,
    ReadBalance,
    ReadTransactions,
    ReadMonthlyFlow,
    SubmitLoanApplication,
    ReadLoans,
    DebitAccount,
    ReverseDebit,
    SubscribePolicy,
    ReadPolicies,
    IssueCertificate,
    CancelPolicy,
    DeclareClaim,
    ReadClaims,
    PriceProduct,
    CheckEligibility
}

/// <summary>KYC tier as the external system understands it. Mapped from M02's own tiers.</summary>
public enum KycLevel
{
    None,
    Simplified,
    Full
}
