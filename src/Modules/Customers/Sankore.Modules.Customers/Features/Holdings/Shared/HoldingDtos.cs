namespace Sankore.Modules.Customers.Features.Holdings.Shared;

/// <summary>The terms of a holding, as the endpoints accept and return them (BNF-B-14).</summary>
public sealed record HoldingSpecificsDto(
    decimal? InsuredCapital = null,
    decimal? Premium = null,
    /// <summary>"Monthly" | "Quarterly" | "Annual" | "Single".</summary>
    string? Periodicity = null,
    int? DurationMonths = null,
    decimal? AnnualRatePercent = null,
    decimal? InitialDeposit = null,
    /// <summary>ISO 4217. Applies to every amount in this record.</summary>
    string? Currency = null,
    DateOnly? EffectiveFrom = null,
    string? Notes = null);

/// <summary>
/// One designation on a holding (BNF-B-15).
/// </summary>
/// <param name="SubjectLabel">
/// Who it names, in words. For a household member or a client this is resolved from their record,
/// so the clause can be read without a second query; for an external person it is the name the
/// agent typed.
/// </param>
public sealed record DesignationDto(
    Guid DesignationId,
    string RoleCode,
    string SubjectType,
    Guid? SubjectId,
    string? SubjectLabel,
    string? ExternalRelationship,
    int Rank,
    decimal? SharePercent,
    string Status,
    string? StatusReason,
    int ClauseVersion,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);

/// <summary>
/// A product a client holds (BNF-B-14).
/// </summary>
/// <param name="CanEdit">
/// Whether this caller's next write would be accepted — false for an imported holding and for
/// anything past the signature. Computed server-side so a screen does not have to re-derive the
/// state machine and get it subtly wrong (BNF-B-20 asks for exactly this).
/// </param>
/// <param name="Designations">The clause AS IT STANDS. Superseded versions are not returned.</param>
public sealed record HoldingDto(
    Guid HoldingId,
    Guid ClientId,
    Guid ProductLineId,
    string ProductCode,
    string ProductCategory,
    string Status,
    string Origin,
    HoldingSpecificsDto Specifics,
    DateTimeOffset? SignedAt,
    string? ExternalRef,
    string? ExternalSystem,
    DateTimeOffset? SyncedAt,
    string? RejectionReason,
    int ClauseVersion,
    bool CanEdit,
    bool IsReadOnly,
    /// <summary>
    /// The endpoint names this holding would currently accept — <c>specifics</c>,
    /// <c>signature-request</c>, <c>signature</c>, <c>submission</c>, <c>return-to-draft</c>,
    /// <c>closure</c>, <c>designations</c> (BNF-B-20).
    ///
    /// <para>
    /// Computed from the aggregate's own transition table, so a screen enables exactly the buttons
    /// that will work. Without it a front-end has to re-implement the state machine, and the first
    /// time the two disagree the agent gets a 409 on a button that looked available.
    /// </para>
    /// </summary>
    IReadOnlyList<string> AvailableActions,
    Guid? SourceLeadId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DesignationDto> Designations);

/// <summary>One designation as a caller submits it (BNF-B-15).</summary>
public sealed record DesignationInput(
    /// <summary>A role code the product line declares. Upper-cased on write.</summary>
    string RoleCode,
    /// <summary>"Client" | "HouseholdMember" | "ExternalPerson".</summary>
    string SubjectType,
    /// <summary>The client id or the household-member id. Null — and only null — for an external person.</summary>
    Guid? SubjectId = null,
    string? ExternalFullName = null,
    string? ExternalRelationship = null,
    int Rank = 1,
    decimal? SharePercent = null);
