namespace Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Error codes this module answers with. In the PublicApi because a consumer module and the
/// front-end both branch on them, and because an adapter in its own assembly must be able to
/// return the shared ones.
///
/// <para>
/// Each constant carries the <see cref="ErrorFamily"/> it is meant to be raised with, in its
/// comment: the family decides retry behaviour, so pairing a transient cause with
/// <c>Functional</c> silently disables the retry that would have fixed it.
/// </para>
/// </summary>
public static class IntegrationErrors
{
    // ── Configuration (Technical) ────────────────────────────────────────────

    /// <summary>Technical. No active connection for the family — nothing to call.</summary>
    public const string NoActiveConnection = "INTEGRATION_NO_ACTIVE_CONNECTION";

    /// <summary>Technical. A connection exists but no adapter is registered for its kind.</summary>
    public const string AdapterNotRegistered = "INTEGRATION_ADAPTER_NOT_REGISTERED";

    /// <summary>Technical. A code has no mapping. The detail names the domain and the code.</summary>
    public const string MappingMissing = "INTEGRATION_MAPPING_MISSING";

    /// <summary>Technical. The connection's credentials are absent from the vault.</summary>
    public const string CredentialMissing = "INTEGRATION_CREDENTIAL_MISSING";

    /// <summary>Technical. Authentication refused by the external system.</summary>
    public const string AuthenticationRefused = "INTEGRATION_AUTHENTICATION_REFUSED";

    /// <summary>Technical. The payload does not satisfy the external contract.</summary>
    public const string PayloadInvalid = "INTEGRATION_PAYLOAD_INVALID";

    /// <summary>Technical. The adapter cannot do this at all — see its capability matrix.</summary>
    public const string CapabilityNotSupported = "INTEGRATION_CAPABILITY_NOT_SUPPORTED";

    /// <summary>
    /// Technical. The adapter exists but its interface specification has not been obtained, so it
    /// deliberately refuses rather than guessing a wire format.
    /// </summary>
    public const string AdapterSpecificationPending = "INTEGRATION_ADAPTER_SPECIFICATION_PENDING";

    // ── Availability (Transient) ─────────────────────────────────────────────

    /// <summary>Transient. No answer within the budget.</summary>
    public const string Timeout = "INTEGRATION_TIMEOUT";

    /// <summary>Transient. 5xx, maintenance window, or an explicit "try later".</summary>
    public const string Unavailable = "INTEGRATION_UNAVAILABLE";

    /// <summary>Transient. The circuit breaker is open for this connection.</summary>
    public const string CircuitOpen = "INTEGRATION_CIRCUIT_OPEN";

    /// <summary>Transient. The on-premise relay agent is not connected.</summary>
    public const string RelayUnavailable = "INTEGRATION_RELAY_UNAVAILABLE";

    /// <summary>
    /// Technical. The write would have to travel to the relay agent as an ORDER rather than as a
    /// file, and this deployment has no such channel: INT-26's platform side is not built.
    ///
    /// <para>
    /// Deliberately distinct from <see cref="RelayUnavailable"/>, which is Transient and means a
    /// built channel has no agent on it right now. Here nothing is coming: retrying for a day
    /// produces the same answer, so the command is rejected and an administrator is told — either
    /// the connection's mode is wrong, or it is waiting on a lot that has not been delivered. The
    /// alternative, before this code existed, was for the dispatcher to resolve an adapter and
    /// make the call DIRECTLY from this process — the one thing <see cref="IntegrationMode.Relay"/>
    /// promises never to happen, since the target is inside the institution's own network.
    /// </para>
    /// </summary>
    public const string RelayCommandChannelMissing = "INTEGRATION_RELAY_COMMAND_CHANNEL_MISSING";

    /// <summary>Transient. The tenant's own rate limit is saturated.</summary>
    public const string RateLimited = "INTEGRATION_RATE_LIMITED";

    // ── Business refusals (Functional) ───────────────────────────────────────

    /// <summary>Functional. The external system already holds this record.</summary>
    public const string Duplicate = "INTEGRATION_DUPLICATE";

    /// <summary>Functional. The external system refused the supporting document.</summary>
    public const string DocumentRefused = "INTEGRATION_DOCUMENT_REFUSED";

    /// <summary>Functional. The product does not exist on the other side.</summary>
    public const string UnknownProduct = "INTEGRATION_UNKNOWN_PRODUCT";

    /// <summary>Functional. The referenced entity is absent from the external system.</summary>
    public const string ExternalEntityNotFound = "INTEGRATION_EXTERNAL_ENTITY_NOT_FOUND";

    /// <summary>Functional. Not enough money on the account to debit (ASS-05).</summary>
    public const string InsufficientFunds = "INTEGRATION_INSUFFICIENT_FUNDS";

    /// <summary>Functional. The account cannot be operated (blocked, closed).</summary>
    public const string AccountNotOperable = "INTEGRATION_ACCOUNT_NOT_OPERABLE";

    /// <summary>Functional. The external system rejected the request on its own rules.</summary>
    public const string Rejected = "INTEGRATION_REJECTED";

    /// <summary>Functional. The answer does not match the documented contract.</summary>
    public const string UnexpectedResponse = "INTEGRATION_UNEXPECTED_RESPONSE";

    // ── Module-level API errors (HTTP side, not adapter results) ─────────────

    public const string ConnectionNotFound = "INTEGRATION_CONNECTION_NOT_FOUND";
    public const string ConnectionNotHealthy = "INTEGRATION_CONNECTION_NOT_HEALTHY";
    public const string CoreBankingConnectionAlreadyActive = "INTEGRATION_CORE_BANKING_ALREADY_ACTIVE";
    public const string CommandNotFound = "INTEGRATION_COMMAND_NOT_FOUND";
    public const string CommandNotReplayable = "INTEGRATION_COMMAND_NOT_REPLAYABLE";
    public const string CommandNotCancellable = "INTEGRATION_COMMAND_NOT_CANCELLABLE";
    public const string MappingNotFound = "INTEGRATION_MAPPING_NOT_FOUND";
    public const string MappingAlreadyExists = "INTEGRATION_MAPPING_ALREADY_EXISTS";
    public const string GapNotFound = "INTEGRATION_GAP_NOT_FOUND";
    public const string GapAlreadyResolved = "INTEGRATION_GAP_ALREADY_RESOLVED";
    public const string ConcurrencyConflict = "INTEGRATION_CONCURRENCY_CONFLICT";
    public const string RelayAgentNotFound = "INTEGRATION_RELAY_AGENT_NOT_FOUND";
    public const string RelayEnrolmentNotPending = "INTEGRATION_RELAY_ENROLMENT_NOT_PENDING";
    public const string RelayEnrolmentExpired = "INTEGRATION_RELAY_ENROLMENT_EXPIRED";
    public const string RelayAgentRevoked = "INTEGRATION_RELAY_AGENT_REVOKED";
    /// <summary>
    /// Transient. The command is valid and its turn has not come: it leaves with the file written
    /// at the connection's next cut-off.
    ///
    /// <para>
    /// Distinct from <see cref="Unavailable"/> on purpose, and the distinction is load-bearing
    /// rather than cosmetic. Waiting for a scheduled hour is <b>not an attempt</b> at the external
    /// system, so it must not consume the dispatcher's retry budget — on a daily cycle, eight
    /// attempts with a one-hour cap are spent in about three hours and every command created in
    /// the morning would be Rejected before its evening file was ever written. The dispatcher
    /// branches on this code to DEFER instead of retrying.
    /// </para>
    /// </summary>
    public const string BatchCycleNotDue = "INTEGRATION_BATCH_CYCLE_NOT_DUE";

    public const string BatchSequenceOutOfOrder = "INTEGRATION_BATCH_SEQUENCE_OUT_OF_ORDER";
    public const string BatchChecksumMismatch = "INTEGRATION_BATCH_CHECKSUM_MISMATCH";
    public const string BatchFileNotFound = "INTEGRATION_BATCH_FILE_NOT_FOUND";
    public const string SettingsInvalid = "INTEGRATION_SETTINGS_INVALID";
    public const string FakeAdapterNotAllowed = "INTEGRATION_FAKE_ADAPTER_NOT_ALLOWED";

    // ── Insurance family (ASS-03 → ASS-12) ──────────────────────────────────

    /// <summary>
    /// The product does not exist, or belongs to another tenant. <b>404, never 403</b> — the same
    /// rule as <see cref="ConnectionNotFound"/>: a 403 would confirm the id names a real product
    /// on the platform, and a catalogue entry names an insurer and a tenant.
    /// </summary>
    public const string InsuranceProductNotFound = "INSURANCE_PRODUCT_NOT_FOUND";

    /// <summary>
    /// This insurer already has a catalogue entry for that product code
    /// (<c>ux_ins_product_insurer_code</c>).
    /// </summary>
    public const string InsuranceProductAlreadyExists = "INSURANCE_PRODUCT_ALREADY_EXISTS";

    /// <summary>
    /// The product cannot be offered right now — ASS-03's fourth criterion. The detail names
    /// every reason, because the agent has to know whether to wait or to call the administrator.
    /// </summary>
    public const string InsuranceProductNotOfferable = "INSURANCE_PRODUCT_NOT_OFFERABLE";

    /// <summary>
    /// The connection named is not an insurance connection. A catalogue entry on a core-banking
    /// connection would resolve a CBS adapter for a policy subscription.
    /// </summary>
    public const string InsuranceConnectionWrongFamily = "INSURANCE_CONNECTION_WRONG_FAMILY";

    /// <summary>
    /// The linked credit product (ASS-03, criterion 3) does not exist in M12's catalogue, or is
    /// not a loan. Checked at write time; readers afterwards degrade to "unlinked".
    /// </summary>
    public const string InsuranceLinkedCreditProductInvalid = "INSURANCE_LINKED_CREDIT_PRODUCT_INVALID";
}
