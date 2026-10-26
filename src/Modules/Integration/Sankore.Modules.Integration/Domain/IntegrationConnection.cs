namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// One tenant's link to one external system (INT-03).
///
/// <para>
/// A tenant has at most ONE active <see cref="IntegrationFamily.CoreBanking"/> connection — a
/// customer cannot be created in two core banking systems — and may have SEVERAL active
/// <see cref="IntegrationFamily.Insurance"/> ones, because an IMF distributes for several
/// insurers. That asymmetry is enforced by a partial unique index, not by this class: two
/// concurrent activations would both pass an in-memory check (ASS-01).
/// </para>
///
/// <para>
/// Credentials are NOT here. The settings object holds coordinates and a vault reference; the
/// value lives in M12's secret store and is never returned by any endpoint, logged, or written
/// to an audit row.
/// </para>
/// </summary>
public sealed class IntegrationConnection : AggregateRoot
{
    public Guid Id { get; private set; }

    public IntegrationFamily Family { get; private set; }

    public IntegrationKind Kind { get; private set; }

    public IntegrationMode Mode { get; private set; }

    /// <summary>Operator-facing name. Several insurance connections need telling apart.</summary>
    public string Name { get; private set; } = string.Empty;

    public ConnectionSettings? Settings { get; private set; }

    /// <summary>
    /// The address an inbound event names this connection by — a WhatsApp phone-number id — copied
    /// from <see cref="ConnectionSettings.ChannelAddress"/> whenever the settings are written.
    ///
    /// <para>
    /// A column of its own, and not a query into the jsonb, because it carries a GLOBAL unique
    /// index over active rows (<c>ux_integration_connection_active_channel_address</c>): an
    /// anonymous webhook has no tenant to filter by, so this is the only way it finds one, and two
    /// active connections answering to the same address would route one institution's customers
    /// to another. Never bindable — it is derived from the settings, never sent.
    /// </para>
    /// </summary>
    public string? ChannelAddress { get; private set; }

    /// <summary>
    /// The on-premise relay this connection goes through, when <see cref="Mode"/> is
    /// <see cref="IntegrationMode.Relay"/>. An opaque reference with no foreign key: a revoked
    /// agent leaves a dangling id and the reader degrades to "relay unavailable" rather than
    /// assuming it resolves.
    ///
    /// <para>
    /// <b>Server-set only.</b> It is set by INT-27's agent enrolment flow — the only place that
    /// holds the agent's identity and can guarantee the agent belongs to this tenant when it mints
    /// the certificate — and it is deliberately absent from every create and update request of
    /// INT-03. Nothing in this module validates the id, so a client able to set it could name
    /// ANOTHER TENANT's agent: that tenant's network would execute this tenant's command payloads
    /// (identity documents, addresses, declared income) while this tenant read the other's SFTP
    /// directories and SQL views. The leak runs in both directions, which is why the field is not
    /// bindable rather than merely checked. Same rule as M13's <c>Lead.LeadSourceConfigId</c>.
    /// </para>
    /// </summary>
    public Guid? RelayAgentId { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset? LastHealthAt { get; private set; }

    /// <summary>Null until a health check has run. False is a failed check, not "never ran".</summary>
    public bool? LastHealthStatus { get; private set; }

    /// <summary>Why the last check failed. Never a credential, never a payload.</summary>
    public string? LastHealthDetail { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    // ── An insurer's mandates to the distributor (insurance lot 2) ──────────
    //
    // Without an express mandate the distributor may not collect premiums in the insurer's name,
    // nor issue a cover note: both functions are refused for this insurer, and the premium is then
    // collected by the insurer itself. Meaningful on an Insurance connection only.

    public bool HasCollectionMandate { get; private set; }

    /// <summary>The mandate's reference (letter, convention), as the evidence a controller asks for.</summary>
    public string? CollectionMandateReference { get; private set; }

    public bool HasCoverNoteMandate { get; private set; }

    public string? CoverNoteMandateReference { get; private set; }

    /// <summary>
    /// The insurer's express agreement that the distributor deducts its commission from the premiums
    /// it remits (lot 4). Without it, the gross premiums are remitted and the commission is paid on
    /// the insurer's statement.
    /// </summary>
    public bool HasCommissionNettingAgreement { get; private set; }

    public string? CommissionNettingReference { get; private set; }

    public Result SetCommissionNetting(bool agreed, string? reference, Guid by, TimeProvider clock)
    {
        if (Family != IntegrationFamily.Insurance)
            return Result.Fail(IntegrationErrors.MandateNotInsurance);
        if (agreed && string.IsNullOrWhiteSpace(reference))
            return Result.Fail(IntegrationErrors.MandateReferenceRequired);

        HasCommissionNettingAgreement = agreed;
        CommissionNettingReference = agreed ? reference!.Trim() : null;
        UpdatedAt = clock.GetUtcNow();
        UpdatedBy = by;
        return Result.Ok();
    }

    public Result SetMandates(
        bool collection, string? collectionReference, bool coverNote, string? coverNoteReference,
        Guid by, TimeProvider clock)
    {
        if (Family != IntegrationFamily.Insurance)
            return Result.Fail(IntegrationErrors.MandateNotInsurance);

        // A mandate the platform cannot point to is a claim nobody can check.
        if ((collection && string.IsNullOrWhiteSpace(collectionReference))
            || (coverNote && string.IsNullOrWhiteSpace(coverNoteReference)))
            return Result.Fail(IntegrationErrors.MandateReferenceRequired);

        HasCollectionMandate = collection;
        CollectionMandateReference = collection ? collectionReference!.Trim() : null;
        HasCoverNoteMandate = coverNote;
        CoverNoteMandateReference = coverNote ? coverNoteReference!.Trim() : null;
        UpdatedAt = clock.GetUtcNow();
        UpdatedBy = by;
        return Result.Ok();
    }

    /// <summary>PostgreSQL xmin — optimistic concurrency, as in every other module.</summary>
    public uint Version { get; private set; }

    private IntegrationConnection() { }

    public static IntegrationConnection Create(
        Guid tenantId,
        IntegrationFamily family,
        IntegrationKind kind,
        IntegrationMode mode,
        string name,
        ConnectionSettings settings,
        Guid createdBy,
        TimeProvider clock,
        Guid? relayAgentId = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A connection name is required.");
        ArgumentNullException.ThrowIfNull(settings);

        // The settings object and the kind must agree: a Temenos row carrying Amplitude settings
        // would resolve an adapter that cannot read its own configuration.
        if (settings.ExpectedKind != kind)
            throw new DomainException(
                $"Settings of kind {settings.ExpectedKind} cannot be stored on a {kind} connection.");

        var now = clock.GetUtcNow();

        return new IntegrationConnection
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            Family = family,
            Kind = kind,
            Mode = mode,
            Name = name.Trim(),
            Settings = settings,
            ChannelAddress = settings.ChannelAddress,
            // Guid.Empty is not an agent: a caller that passes it means "none", and storing it
            // would create a relay reference nothing can resolve while looking like a real one.
            RelayAgentId = relayAgentId == Guid.Empty ? null : relayAgentId,
            IsActive = false,
            CreatedAt = now,
            CreatedBy = createdBy,
            UpdatedAt = now,
        };
    }

    /// <summary>True when a successful health check is recent enough to allow activation.</summary>
    public bool HasPassedHealthCheck => LastHealthStatus == true;

    /// <summary>
    /// Re-points the connection, and <b>invalidates its health verdict when the coordinates
    /// actually changed</b>.
    ///
    /// <para>
    /// The health columns describe one thing: the answer the far end gave to the configuration
    /// that was checked. Carry them across an edit and they describe coordinates that no longer
    /// exist — an administrator moves a <c>baseUrl</c> to the wrong host, or switches the auth
    /// mode, and the operations screen keeps reporting the connection healthy while every command
    /// fails one at a time. A green column that is merely out of date is worse than an empty one,
    /// because it is the column somebody checks first.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="IsActive"/> is deliberately left alone.</b> Deactivating would be the safer
    /// reflex and the wrong behaviour: this method also carries the NAME, so renaming a connection
    /// would silently stop a tenant's integration. What invalidating the verdict does buy is the
    /// thing that matters — <see cref="HasPassedHealthCheck"/> goes false, so the connection cannot
    /// be activated (or re-activated) until somebody actually checks it, and the screen stops
    /// claiming something it no longer knows. A connection that was already live keeps draining
    /// its queue, and a wrong edit surfaces where it should: in the rejection queue, not as a
    /// surprise outage.
    /// </para>
    ///
    /// <para>
    /// A pure rename changes nothing that was checked, so it keeps the verdict. Without that
    /// exception every label correction would ask an administrator to re-run a health check,
    /// which trains people to click past it.
    /// </para>
    /// </summary>
    public void UpdateSettings(
        string name, IntegrationMode mode, ConnectionSettings settings, Guid? relayAgentId,
        Guid updatedBy, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A connection name is required.");
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);

        if (settings.ExpectedKind != Kind)
            throw new DomainException(
                $"Settings of kind {settings.ExpectedKind} cannot be stored on a {Kind} connection.");

        var normalisedAgent = relayAgentId == Guid.Empty ? null : relayAgentId;

        // Settings are records, so this is value equality over the concrete type — the same
        // comparison ConnectionSettingsComparer makes for EF's change tracker, which needs an
        // explicit one only because the property is stored through a value converter.
        var rePointed = mode != Mode
            || normalisedAgent != RelayAgentId
            || !Equals(settings, Settings);

        Name = name.Trim();
        Mode = mode;
        Settings = settings;
        ChannelAddress = settings.ChannelAddress;
        RelayAgentId = normalisedAgent;
        UpdatedBy = updatedBy;
        UpdatedAt = clock.GetUtcNow();

        if (!rePointed) return;

        LastHealthAt = null;
        LastHealthStatus = null;
        LastHealthDetail =
            "The connection was re-pointed; no health check has run against these coordinates yet.";
    }

    public void RecordHealth(IntegrationHealth health, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(health);

        LastHealthAt = health.CheckedAt;
        LastHealthStatus = health.IsHealthy;
        LastHealthDetail = health.Detail;
        UpdatedAt = clock.GetUtcNow();
    }

    /// <summary>
    /// Activation requires a passed health check (INT-03). Checked here rather than only in the
    /// handler because activation is what makes this connection the one every command of the
    /// tenant will be sent through.
    /// </summary>
    public Result Activate(Guid actor, TimeProvider clock)
    {
        if (!HasPassedHealthCheck)
            return Result.Fail(IntegrationErrors.ConnectionNotHealthy);

        IsActive = true;
        UpdatedBy = actor;
        UpdatedAt = clock.GetUtcNow();
        return Result.Ok();
    }

    /// <summary>
    /// Deactivated, never deleted: commands, references and call logs point at it, and a deleted
    /// row would make a year of audit trail unreadable.
    /// </summary>
    public void Deactivate(Guid actor, TimeProvider clock)
    {
        IsActive = false;
        UpdatedBy = actor;
        UpdatedAt = clock.GetUtcNow();
    }
}
