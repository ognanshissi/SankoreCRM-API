namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One tenant's call-recording policy (TEL-B-15).
///
/// <para>
/// <b>In THIS module's schema and not in M12's <c>compliance_settings</c>, although the permission
/// that writes it is a compliance one.</b> The repo's own rule for that table is that it holds the
/// values THREE modules read — a GIIN, a PEP window, an auto-approval ceiling — and a setting with
/// one reader belongs to the module that enforces it. Nothing outside telephony reads this, and a
/// row in M12 would make the integration module depend on the administration module to learn
/// whether it may keep a file it alone fetches, stores and purges. What is borrowed from
/// compliance is the PERMISSION, which says who may decide, not where the decision lives.
/// </para>
///
/// <para>
/// <b>No row means <see cref="TelRecordingMode.Disabled"/>, and that is why there is no seeder.</b>
/// Every other per-tenant referential in this family is seeded because an empty table is a broken
/// feature; here an absent row is the correct and safest state, so seeding one would be inventing
/// a policy nobody stated. <c>TelRecordingPolicy.DisabledFor</c> is what a reader gets.
/// </para>
/// </summary>
public sealed class TelRecordingPolicy : AggregateRoot
{
    private TelRecordingPolicy() { }

    public TelRecordingMode Mode { get; private set; }

    /// <summary>
    /// Why this institution is allowed to hold a recording — the lawful basis, in the compliance
    /// officer's own words. Required for any mode but <c>Disabled</c>.
    ///
    /// <para>
    /// Free text and not an enum, deliberately. The bases differ by country and by regulator, the
    /// wording is what a supervisor asks to see, and an enum would make the platform's list of
    /// lawful bases a constraint on what an institution may claim. It is copied onto every
    /// recording at retrieval and frozen there.
    /// </para>
    /// </summary>
    public string? LegalBasis { get; private set; }

    /// <summary>
    /// What the recordings are FOR. Separate from the basis because they are different questions —
    /// "we may" and "we need to" — and a purpose limitation nobody wrote down is a purpose
    /// limitation nobody can be held to.
    /// </summary>
    public string? Purpose { get; private set; }

    /// <summary>
    /// How long a recording is kept, in days. Frozen onto each recording at retrieval, so
    /// shortening this does not reach back and lengthening it does not either — see
    /// <see cref="TelRecording"/>.
    /// </summary>
    public int RetentionDays { get; private set; }

    /// <summary>
    /// Whether to ask the provider to delete its own copy once SANKORE holds one (TEL-B-16).
    ///
    /// <para>
    /// A separate switch from the mode, because an institution can want a copy without being able
    /// to take the provider's: the deletion only happens where the adapter claims
    /// <c>RecordingDeletion</c>, and a provider without it is an ordinary provider whose retention
    /// is then its own contract's business.
    /// </para>
    /// </summary>
    public bool DeleteAtProvider { get; private set; }

    /// <summary>
    /// Largest retrieved recording, in seconds. The mitigation named in the plan's eighth
    /// deviation: <c>AesGcm</c> is single-shot in .NET and <c>IObjectBackend</c> only knows
    /// <c>byte[]</c>, so the whole file is materialised in the process whatever the signatures
    /// say. A ceiling on the DURATION rather than on the bytes, because that is the figure a
    /// compliance officer can reason about — a provider reports it before the download starts.
    /// </summary>
    public int MaxDurationSeconds { get; private set; }

    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    /// <summary>Six hours, the ceiling a tenant gets unless it states another.</summary>
    public const int DefaultMaxDurationSeconds = 6 * 60 * 60;

    /// <summary>Ninety days. Short enough to be defensible, long enough to settle a dispute.</summary>
    public const int DefaultRetentionDays = 90;

    /// <summary>
    /// What a tenant with no row has. Not persisted and never saved: it exists so every reader of
    /// the policy handles one shape, and so "no policy" cannot be read as "no opinion".
    /// </summary>
    public static TelRecordingPolicy DisabledFor(Guid tenantId) => new()
    {
        TenantId = tenantId,
        Mode = TelRecordingMode.Disabled,
        RetentionDays = DefaultRetentionDays,
        MaxDurationSeconds = DefaultMaxDurationSeconds,
    };

    public static TelRecordingPolicy Create(Guid tenantId, Guid updatedBy, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");

        return new TelRecordingPolicy
        {
            TenantId = tenantId,
            Mode = TelRecordingMode.Disabled,
            RetentionDays = DefaultRetentionDays,
            MaxDurationSeconds = DefaultMaxDurationSeconds,
            UpdatedBy = updatedBy,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Applies a compliance officer's decision.
    ///
    /// <para>
    /// <b>Any mode but <c>Disabled</c> requires BOTH a legal basis and a purpose.</b> That is the
    /// one rule this aggregate exists to hold: a recording kept without a stated basis is a file
    /// nobody can justify holding, and a basis without a purpose is a justification nobody can be
    /// held to. Switching BACK to <c>Disabled</c> keeps both — the recordings already taken are
    /// still held under them, and blanking the words would leave those files unexplained.
    /// </para>
    /// </summary>
    public Result Apply(
        TelRecordingMode mode,
        string? legalBasis,
        string? purpose,
        int retentionDays,
        bool deleteAtProvider,
        int maxDurationSeconds,
        Guid updatedBy,
        DateTimeOffset now)
    {
        if (mode != TelRecordingMode.Disabled)
        {
            if (string.IsNullOrWhiteSpace(legalBasis))
                return Result.Fail(TelephonyErrors.RecordingLegalBasisRequired);

            if (string.IsNullOrWhiteSpace(purpose))
                return Result.Fail(TelephonyErrors.RecordingPurposeRequired);
        }

        if (retentionDays <= 0)
            return Result.Fail(TelephonyErrors.RecordingRetentionInvalid);

        if (maxDurationSeconds <= 0)
            return Result.Fail(TelephonyErrors.RecordingDurationCeilingInvalid);

        Mode = mode;

        // Only ever written, never blanked: see the remarks. A switch to Disabled leaves the words
        // that explain the files already held.
        if (!string.IsNullOrWhiteSpace(legalBasis)) LegalBasis = legalBasis.Trim();
        if (!string.IsNullOrWhiteSpace(purpose)) Purpose = purpose.Trim();

        RetentionDays = retentionDays;
        DeleteAtProvider = deleteAtProvider;
        MaxDurationSeconds = maxDurationSeconds;
        UpdatedBy = updatedBy;
        UpdatedAt = now;

        return Result.Ok();
    }

    /// <summary>
    /// Whether this call's recording is to be kept.
    ///
    /// <para>
    /// The whole of the mode's meaning, in one place, so the fetch job cannot hold a second
    /// opinion: <c>Disabled</c> keeps nothing, <c>AllCalls</c> keeps what the provider recorded,
    /// and <c>OnDemand</c> keeps only what an agent asked for.
    /// </para>
    /// </summary>
    public bool Keeps(TelCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        return Mode switch
        {
            TelRecordingMode.AllCalls => true,
            TelRecordingMode.OnDemand => call.KeepRecording,
            _ => false,
        };
    }
}
