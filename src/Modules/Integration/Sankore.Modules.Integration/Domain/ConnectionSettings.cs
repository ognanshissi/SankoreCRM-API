namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Polymorphic, per-kind connection settings, stored as jsonb with a <c>$kind</c> discriminator.
///
/// <para>
/// <b>No field may carry a name evoking a secret</b> (secret, password, token, apiKey): the
/// credentials live in the M12 vault and this object holds only non-secret coordinates plus the
/// vault reference. The same rule M13's <c>SourceSettings</c> states, for the same reason — this
/// object is returned by the API, and a settings record is exactly where a password ends up by
/// accident.
/// </para>
///
/// <para>
/// Serialization is handled by <c>ConnectionSettingsConverter</c> (EF) and
/// <c>ConnectionSettingsJsonConverter</c> (HTTP); the discriminator is injected and inferred by
/// those, so no <c>[JsonPolymorphic]</c> attributes are needed.
/// </para>
/// </summary>
public abstract record ConnectionSettings
{
    /// <summary>Schema version, for forward-compatible upgrades.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The kind these settings belong to. A mismatch with the row is refused.</summary>
    public abstract IntegrationKind ExpectedKind { get; }

    /// <summary>
    /// Outbound calls per minute this tenant may make on this connection (INT-09). Zero means
    /// unlimited. Lives in settings rather than in a tenant parameter because the limit belongs
    /// to the far end: it is what the IMF's CBS licence allows, not a SANKORE policy.
    /// </summary>
    public int RateLimitPerMinute { get; init; }

    /// <summary>Consecutive failures that open the circuit. Zero falls back to the default.</summary>
    public int CircuitBreakerFailureThreshold { get; init; }

    /// <summary>How long the circuit stays open before a trial call. Zero takes the default.</summary>
    public int CircuitBreakerBreakSeconds { get; init; }

    /// <summary>Per-call budget. Clamped by the adapter; zero takes the default.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>
    /// How often each stream is synchronised, in minutes (INT-20).
    ///
    /// <para>
    /// Here rather than in a tenant-parameter table, and that is the specified shape: INT-20 asks
    /// for a period configurable "par flux et par tenant", and a connection already belongs to
    /// exactly one tenant. It also belongs on the far end — how often an IMF's CBS tolerates being
    /// polled is a property of that installation, not a SANKORE policy.
    /// </para>
    ///
    /// <para>
    /// A stream absent from the map takes <see cref="DefaultSyncIntervalMinutes"/>. Absent is NOT
    /// "never": a connection created before a stream existed must still be swept, or adding a
    /// stream would silently leave every existing tenant unsynchronised on it.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<SyncStream, int>? SyncIntervalMinutes { get; init; }

    /// <summary>
    /// Fallback periods, in minutes. Reads that feed a screen are swept hourly; transaction and
    /// loan history is bulkier and changes more slowly, so it is swept every four hours — the
    /// figure a counter needs in the second is the LIVE balance of INT-15, never this sweep.
    /// </summary>
    public static readonly IReadOnlyDictionary<SyncStream, int> DefaultSyncIntervalMinutes =
        new Dictionary<SyncStream, int>
        {
            [SyncStream.Customers] = 60,
            [SyncStream.Accounts] = 60,
            [SyncStream.Transactions] = 240,
            [SyncStream.Loans] = 240,
            [SyncStream.Policies] = 60,
            [SyncStream.Claims] = 60,
        };

    /// <summary>
    /// The period in force for one stream. Zero or a negative stored value falls back to the
    /// default rather than meaning "continuously": a misconfigured zero would otherwise turn the
    /// orchestrator into a hot loop against somebody's production CBS.
    /// </summary>
    public TimeSpan SyncIntervalFor(SyncStream stream)
    {
        var minutes = SyncIntervalMinutes is not null
                      && SyncIntervalMinutes.TryGetValue(stream, out var configured)
                      && configured > 0
            ? configured
            : DefaultSyncIntervalMinutes[stream];

        return TimeSpan.FromMinutes(minutes);
    }
}

/// <summary>Settings common to every file-based connection (INT-24/INT-25).</summary>
public abstract record BatchCapableSettings : ConnectionSettings
{
    /// <summary>Local time of day at which the outbound file is produced.</summary>
    public TimeOnly CutOffTime { get; init; } = new(18, 0);

    /// <summary>Code page of the generated file. West-African CBS deployments are rarely UTF-8.</summary>
    public string FileEncoding { get; init; } = "UTF-8";

    public string FieldSeparator { get; init; } = ";";

    /// <summary>Directory on the SFTP server where we deposit. Never a credential.</summary>
    public string? OutboundDirectory { get; init; }

    /// <summary>Directory we poll for acknowledgements and extractions.</summary>
    public string? InboundDirectory { get; init; }

    public string? SftpHost { get; init; }

    public int SftpPort { get; init; } = 22;

    public string? SftpUsername { get; init; }

    /// <summary>Vault reference of the SFTP password or private key. Never the value.</summary>
    public string? SftpCredentialVaultRef { get; init; }

    /// <summary>Days an acknowledged file is kept before purge.</summary>
    public int RetentionDays { get; init; } = 30;

    /// <summary>Hours a Batched command may wait for its acknowledgement before alerting.</summary>
    public int AckTimeoutHours { get; init; } = 48;
}

/// <summary>Temenos Transact — Party and Holdings APIs (INT-12/INT-13).</summary>
public sealed record TemenosSettings : ConnectionSettings
{
    public override IntegrationKind ExpectedKind => IntegrationKind.Temenos;

    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>OAuth 2.0 client-credentials, or a long-lived token held in the vault.</summary>
    public TemenosAuthMode AuthMode { get; init; } = TemenosAuthMode.OAuthClientCredentials;

    public string? TokenEndpoint { get; init; }

    public string? OAuthClientId { get; init; }

    public string? OAuthScope { get; init; }

    /// <summary>Vault reference of the client secret or the static token.</summary>
    public string? CredentialVaultRef { get; init; }

    /// <summary>Transact company / branch the calls are made in the name of.</summary>
    public string? CompanyId { get; init; }

    /// <summary>API version segment, e.g. <c>v2.0.0</c>.</summary>
    public string? ApiVersion { get; init; }
}

public enum TemenosAuthMode
{
    OAuthClientCredentials,
    StaticToken
}

/// <summary>
/// Amplitude (SBS). The version decides everything: Amplitude Up exposes API services, earlier
/// releases do not, and the adapter falls back to the batch socle (INT-31).
/// </summary>
public sealed record AmplitudeSettings : BatchCapableSettings
{
    public override IntegrationKind ExpectedKind => IntegrationKind.Amplitude;

    public AmplitudeVersion AmplitudeVersion { get; init; } = AmplitudeVersion.Legacy;

    public string? BaseUrl { get; init; }

    public string? CredentialVaultRef { get; init; }
}

public enum AmplitudeVersion
{
    /// <summary>Pre-Up: no API, batch files only.</summary>
    Legacy,

    /// <summary>Amplitude Up: API services available.</summary>
    Up
}

/// <summary>SAB AT through Open SAB, authenticated by API key (INT-32).</summary>
public sealed record SabSettings : ConnectionSettings
{
    public override IntegrationKind ExpectedKind => IntegrationKind.Sab;

    public string BaseUrl { get; init; } = string.Empty;

    public string? CredentialVaultRef { get; init; }

    /// <summary>
    /// Open SAB's <c>Entity</c>. Required on a multi-IMF network such as CIF, where one
    /// installation serves several institutions and every call must say which.
    /// </summary>
    public string? Entity { get; init; }
}

/// <summary>Perfect Vision — batch only, plus an optional read-only SQL view (INT-28).</summary>
public sealed record PerfectVisionSettings : BatchCapableSettings
{
    public override IntegrationKind ExpectedKind => IntegrationKind.PerfectVision;

    /// <summary>
    /// Name of the read-only view the relay agent may query for balances. Null means the
    /// snapshot is the only source.
    /// </summary>
    public string? BalanceViewName { get; init; }
}

/// <summary>ORASS®Suite (ORSYS) — insurance back-office (ASS-06).</summary>
public sealed record OrassSettings : BatchCapableSettings
{
    public override IntegrationKind ExpectedKind => IntegrationKind.Orass;

    public string? BaseUrl { get; init; }

    public string? CredentialVaultRef { get; init; }

    /// <summary>The IMF's intermediary / introducer code at the insurer.</summary>
    public string? IntermediaryCode { get; init; }

    /// <summary>IARD (non-life) or Vie (life). Decides which ORASS module answers.</summary>
    public OrassBranch Branch { get; init; } = OrassBranch.Iard;
}

public enum OrassBranch
{
    Iard,
    Vie
}

/// <summary>
/// The in-memory adapter. Development only — <c>IntegrationModule</c> refuses to register it
/// outside Development, because a deployment that reached it would report writes that never
/// happened (INT-10).
/// </summary>
public sealed record FakeSettings : ConnectionSettings
{
    public override IntegrationKind ExpectedKind => IntegrationKind.Fake;

    /// <summary>Which family the fake stands in for.</summary>
    public IntegrationFamily Family { get; init; } = IntegrationFamily.CoreBanking;

    /// <summary>Forces every call to answer this error code, for exercising the failure paths.</summary>
    public string? ForcedErrorCode { get; init; }

    /// <summary>Family of the forced error. Ignored when no code is forced.</summary>
    public ErrorFamily? ForcedErrorFamily { get; init; }
}
