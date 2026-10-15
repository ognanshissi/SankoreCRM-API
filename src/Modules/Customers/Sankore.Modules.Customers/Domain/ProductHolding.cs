namespace Sankore.Modules.Customers.Domain;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// A product a client holds, or is subscribing to (BNF-B-14) — the CRM's own record of a
/// subscription, in <c>customers.product_holdings</c>.
///
/// <para>
/// <b>What it is not.</b> It does not duplicate Integration's read models: <c>InsuranceSubscription</c>
/// (the subscription saga) and <c>PolicyRecord</c> (what the insurer actually holds) stay where they
/// are, and the link between the two worlds is <see cref="ExternalRef"/>, filled when the back
/// office answers. This table is the CAPTURE — what an agent entered, what the client signed, and
/// who is designated on it.
/// </para>
///
/// <para>
/// <b>Why designations are children and not their own aggregate.</b> Every designation invariant is
/// about the SET: the cardinality a role allows, and the shares of a rank summing to 100%. Reached
/// through the holding, they are checked once, in <see cref="Designate"/>; as a separate aggregate
/// they would be checked by whichever handler remembered.
/// </para>
/// </summary>
public sealed class ProductHolding : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid ClientId { get; private set; }

    /// <summary>
    /// The M12 product line — a <c>ProductSpeciality</c> id. An OPAQUE reference with no foreign
    /// key (another schema), so a retired line leaves a resolvable-looking id and every reader
    /// degrades rather than assuming.
    /// </summary>
    public Guid ProductLineId { get; private set; }

    /// <summary>
    /// The line's code and category as they were AT SUBSCRIPTION. A snapshot, not a lookup: a
    /// product renamed or recategorised three years later must not change what a signed contract
    /// says it was. The live catalogue stays the source for anything about the product TODAY.
    /// </summary>
    public string ProductCode { get; private set; } = default!;

    public string ProductCategory { get; private set; } = default!;

    public HoldingStatus Status { get; private set; }
    public HoldingOrigin Origin { get; private set; }

    public HoldingSpecifics Specifics { get; private set; } = HoldingSpecifics.None;

    /// <summary>
    /// The lead this subscription came from, when it came from one (BNF-B-24). Opaque, no FK.
    /// </summary>
    public Guid? SourceLeadId { get; private set; }

    /// <summary>
    /// The commercial request this subscription came from, when it came from one. Opaque, no FK —
    /// an <c>Opportunity</c> lives in the leads schema.
    ///
    /// <para>
    /// Distinct from <see cref="SourceLeadId"/> and NOT a replacement for it: a lead is a prospect
    /// being converted, an opportunity is a request on a client who already exists. A holding
    /// carries one or the other, and the pair cannot be collapsed because the two ids come from
    /// different tables and mean different things to whoever is reading the trail.
    /// </para>
    /// </summary>
    public Guid? SourceOpportunityId { get; private set; }

    // ── Signature and synchronisation (BNF-B-14, BNF-B-16) ───────────────────

    /// <summary>
    /// When the client signed. BNF-B-16 makes this the gate before anything reaches a back office,
    /// which is why <see cref="SubmitForSync"/> refuses without it rather than trusting the status.
    /// </summary>
    public DateTimeOffset? SignedAt { get; private set; }

    /// <summary>Where the signature evidence lives. Required to sign.</summary>
    public string? SignatureReference { get; private set; }

    /// <summary>
    /// The back office's own identifier, set when it accepts. Null until then, and null forever for
    /// a line that is pushed nowhere.
    /// </summary>
    public string? ExternalRef { get; private set; }

    /// <summary>Which system answered — an <c>IntegrationKind</c> name. Null when CRM-only.</summary>
    public string? ExternalSystem { get; private set; }

    public DateTimeOffset? SyncedAt { get; private set; }

    /// <summary>Why the back office refused, in words an agent can act on.</summary>
    public string? RejectionReason { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }
    public string? CloseReason { get; private set; }

    // ── Designations (BNF-B-15) ──────────────────────────────────────────────

    private readonly List<BeneficiaryDesignation> _designations = [];

    /// <summary>
    /// Every designation ever recorded, superseded versions included. Read
    /// <see cref="CurrentDesignations"/> for the clause as it stands.
    /// </summary>
    public IReadOnlyList<BeneficiaryDesignation> Designations => _designations.AsReadOnly();

    public IEnumerable<BeneficiaryDesignation> CurrentDesignations
        => _designations.Where(d => d.IsCurrent);

    /// <summary>
    /// Which version of the beneficiary clause is in force. Starts at 1 and only ever increases —
    /// on an <c>Active</c> holding every change is an avenant (BNF-B-15), so the version is what a
    /// back office and a reconciliation run compare.
    /// </summary>
    public int ClauseVersion { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>. The module's existing concurrency convention.</summary>
    public uint Version { get; private set; }

    /// <summary>
    /// Whether the CRM may change anything at all. A holding projected from a back office is
    /// read-only — the authoritative copy is elsewhere, and a CRM edit would be silently overwritten
    /// by the next reconciliation with the agent never learning their change was lost.
    /// </summary>
    public bool IsReadOnly => Origin == HoldingOrigin.ImportedFromBackOffice;

    /// <summary>Whether the terms may still be edited: only before the client signs.</summary>
    public bool IsEditable => !IsReadOnly && Status is HoldingStatus.Draft or HoldingStatus.PendingSignature;

    /// <summary>
    /// The agent-driven actions this holding would currently accept (BNF-B-20), as the endpoint
    /// names them.
    ///
    /// <para>
    /// Computed from the SAME transition table the methods go through, so a screen can enable
    /// exactly the buttons that will work instead of re-deriving the state machine and getting it
    /// subtly wrong — which is what BNF-B-20 asks for. A read-only holding offers none.
    /// </para>
    ///
    /// <para>
    /// <c>signature</c> is listed by the status rather than by the table, because
    /// <see cref="Sign"/> deliberately does NOT move the status: a holding in
    /// <c>PendingSignature</c> accepts a signature whether or not one is already recorded, and
    /// re-signing is how a correction is captured.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AvailableActions
    {
        get
        {
            if (IsReadOnly) return [];

            var actions = new List<string>(5);

            if (IsEditable) actions.Add("specifics");
            if (Allowed[Status].Contains(HoldingStatus.PendingSignature)) actions.Add("signature-request");
            if (Status == HoldingStatus.PendingSignature) actions.Add("signature");
            if (Allowed[Status].Contains(HoldingStatus.PendingSync) && SignedAt is not null) actions.Add("submission");
            if (Allowed[Status].Contains(HoldingStatus.Draft)) actions.Add("return-to-draft");
            if (Allowed[Status].Contains(HoldingStatus.Closed)) actions.Add("closure");

            // The clause follows Designate's own refusals rather than the transition table: it is
            // editable in every live state EXCEPT PendingSync, where the request is already in a
            // queue (BNF-B-15).
            if (Status is not (HoldingStatus.PendingSync or HoldingStatus.Closed))
                actions.Add("designations");

            return actions;
        }
    }

    private ProductHolding() { } // EF Core

    // ── Factories ────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a draft subscription captured in the CRM.
    /// </summary>
    public static ProductHolding Subscribe(
        Guid tenantId,
        Guid clientId,
        BeneficiaryPolicyDto line,
        HoldingSpecifics specifics,
        Guid createdBy,
        DateTimeOffset now,
        Guid? sourceLeadId = null,
        Guid? sourceOpportunityId = null)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new ProductHolding
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            ProductLineId = line.ProductLineId,
            ProductCode = line.ProductCode,
            ProductCategory = line.Category,
            Status = HoldingStatus.Draft,
            Origin = HoldingOrigin.CrmSubscription,
            Specifics = specifics ?? HoldingSpecifics.None,
            SourceLeadId = sourceLeadId,
            SourceOpportunityId = sourceOpportunityId,
            ClauseVersion = 1,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Projects a product the client already held in a back office (BNF-B-14) — created
    /// <c>Active</c> and read-only, by an Integration consumer and never by an endpoint.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a route through <see cref="Subscribe"/> plus transitions: an imported
    /// holding was never a draft here and never signed here, and walking it through the state
    /// machine would invent a signature date and a sync timestamp that nothing in this system
    /// witnessed.
    /// </remarks>
    public static ProductHolding Imported(
        Guid tenantId,
        Guid clientId,
        BeneficiaryPolicyDto line,
        HoldingSpecifics specifics,
        string externalSystem,
        string externalRef,
        Guid recordedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (string.IsNullOrWhiteSpace(externalRef))
            throw new DomainException(
                "An imported holding needs the back office's own reference.",
                "ProductHolding.ExternalRef.Required");

        return new ProductHolding
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            ProductLineId = line.ProductLineId,
            ProductCode = line.ProductCode,
            ProductCategory = line.Category,
            Status = HoldingStatus.Active,
            Origin = HoldingOrigin.ImportedFromBackOffice,
            Specifics = specifics ?? HoldingSpecifics.None,
            ExternalSystem = externalSystem.Trim(),
            ExternalRef = externalRef.Trim(),
            SyncedAt = now,
            ClauseVersion = 1,
            CreatedBy = recordedBy,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // ── The state machine ────────────────────────────────────────────────────

    /// <summary>
    /// The only description of which transitions exist. Handlers never compare statuses
    /// themselves — the lesson M02's <c>KycFile</c> paid for.
    /// </summary>
    private static readonly Dictionary<HoldingStatus, HoldingStatus[]> Allowed = new()
    {
        [HoldingStatus.Draft]            = [HoldingStatus.PendingSignature, HoldingStatus.Closed],
        [HoldingStatus.PendingSignature] = [HoldingStatus.PendingSync, HoldingStatus.Draft, HoldingStatus.Closed],
        // No route back to PendingSignature: once it has been handed to a back office the request
        // is out of our hands, and re-signing it would produce a second subscription downstream.
        [HoldingStatus.PendingSync]      = [HoldingStatus.Active, HoldingStatus.Rejected],
        [HoldingStatus.Active]           = [HoldingStatus.Closed],
        // A rejection is fixable: back to Draft, corrected, signed again.
        [HoldingStatus.Rejected]         = [HoldingStatus.Draft, HoldingStatus.Closed],
        [HoldingStatus.Closed]           = [],
    };

    private Result MoveTo(HoldingStatus next, DateTimeOffset now)
    {
        if (IsReadOnly)
            return Result.Fail(CustomerErrors.HoldingReadOnly);

        if (!Allowed[Status].Contains(next))
            return Result.Fail(CustomerErrors.HoldingInvalidTransition);

        Status = next;
        UpdatedAt = now;
        return Result.Ok();
    }

    /// <summary>Replaces the terms. Only while nothing has been promised.</summary>
    public Result UpdateSpecifics(HoldingSpecifics specifics, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(specifics);

        if (!IsEditable)
            return Result.Fail(IsReadOnly
                ? CustomerErrors.HoldingReadOnly
                : CustomerErrors.HoldingNotEditable);

        Specifics = specifics;
        UpdatedAt = now;
        return Result.Ok();
    }

    /// <summary>
    /// Declares the file complete and awaiting the client's signature.
    /// </summary>
    /// <param name="missingDesignations">
    /// Roles the product line requires and the clause does not satisfy, computed by the caller from
    /// the policy. Non-empty refuses: a contract sent for signature with no beneficiary where the
    /// line demands one is a contract the insurer will reject after the client has signed it.
    /// </param>
    public Result RequestSignature(
        IReadOnlyList<string> missingDesignations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(missingDesignations);

        if (missingDesignations.Count > 0)
            return Result.Fail(CustomerErrors.DesignationRequiredRoleMissing);

        return MoveTo(HoldingStatus.PendingSignature, now);
    }

    /// <summary>Records the client's signature and its evidence (BNF-B-16's gate).</summary>
    public Result Sign(string signatureReference, DateTimeOffset signedAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(signatureReference))
            return Result.Fail(CustomerErrors.SignatureReferenceRequired);

        if (Status != HoldingStatus.PendingSignature)
            return Result.Fail(CustomerErrors.HoldingInvalidTransition);

        SignedAt = signedAt;
        SignatureReference = signatureReference.Trim();
        UpdatedAt = now;
        return Result.Ok();
    }

    /// <summary>
    /// Queues the holding for its back office.
    /// </summary>
    /// <remarks>
    /// The signature is re-checked HERE and not inferred from the status, because
    /// <see cref="Sign"/> leaves the status untouched: a holding can sit in
    /// <c>PendingSignature</c> either signed or not, and BNF-B-16 makes the signature — not the
    /// status — the thing that must be true before anything is sent.
    /// </remarks>
    public Result SubmitForSync(DateTimeOffset now)
    {
        if (SignedAt is null || SignatureReference is null)
            return Result.Fail(CustomerErrors.SignatureRequired);

        return MoveTo(HoldingStatus.PendingSync, now);
    }

    /// <summary>The back office accepted it.</summary>
    public Result ConfirmSync(string externalSystem, string externalRef, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(externalRef))
            return Result.Fail(CustomerErrors.ExternalRefRequired);

        var moved = MoveTo(HoldingStatus.Active, now);
        if (moved.IsFailure) return moved;

        ExternalSystem = string.IsNullOrWhiteSpace(externalSystem) ? null : externalSystem.Trim();
        ExternalRef = externalRef.Trim();
        SyncedAt = now;
        RejectionReason = null;
        return Result.Ok();
    }

    /// <summary>The back office refused it, with a reason the agent can act on.</summary>
    public Result RecordRejection(string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        var moved = MoveTo(HoldingStatus.Rejected, now);
        if (moved.IsFailure) return moved;

        RejectionReason = reason.Trim();
        return Result.Ok();
    }

    /// <summary>Sends a rejected holding back to the agent for correction.</summary>
    public Result ReturnToDraft(DateTimeOffset now)
    {
        var moved = MoveTo(HoldingStatus.Draft, now);
        if (moved.IsFailure) return moved;

        // The signature does not survive a correction: what the client signed is no longer what
        // would be sent. Keeping it would let a corrected contract reach a back office carrying
        // evidence of consent to the previous terms.
        SignedAt = null;
        SignatureReference = null;
        return Result.Ok();
    }

    public Result Close(string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CustomerErrors.ReasonRequired);

        var moved = MoveTo(HoldingStatus.Closed, now);
        if (moved.IsFailure) return moved;

        ClosedAt = now;
        CloseReason = reason.Trim();

        // The clause goes with the contract. Closed and not deleted: a claim filed later asks who
        // was designated at the time, and the answer has to survive the contract ending.
        foreach (var designation in _designations.Where(d => d.IsCurrent))
            designation.Close("HOLDING_CLOSED", now);

        return Result.Ok();
    }

    // ── Designations (BNF-B-15) ──────────────────────────────────────────────

    /// <summary>
    /// Replaces the beneficiary clause with <paramref name="requested"/>, as ONE atomic set.
    /// </summary>
    /// <remarks>
    /// <b>A whole-clause replacement and not an add/remove pair, deliberately.</b> The invariants
    /// are about the set — a role's cardinality and the shares of a rank totalling 100% — so an
    /// add-one endpoint would either refuse every intermediate state (making a three-beneficiary
    /// clause impossible to enter) or accept an invalid one. The agent composes the clause and
    /// submits it.
    ///
    /// <para>
    /// <b>On an ACTIVE holding this writes a new VERSION</b> (BNF-B-15): the previous designations
    /// are closed with <c>ValidTo</c> and the new ones carry <c>ClauseVersion + 1</c>. An avenant,
    /// not an edit — the back office has the old clause, and the version is what tells it which one
    /// it is being sent.
    /// </para>
    /// </remarks>
    /// <param name="roles">
    /// The product line's accepted roles, from its <c>BeneficiaryPolicy</c>. The caller resolves
    /// them; this method enforces them, so a line that accepts no designation refuses every one.
    /// </param>
    public Result<int> Designate(
        IReadOnlyList<DesignationRequest> requested,
        IReadOnlyList<DesignationRoleDto> roles,
        Guid recordedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(roles);

        if (IsReadOnly)
            return Result.Fail<int>(CustomerErrors.HoldingReadOnly);

        if (Status is HoldingStatus.Closed)
            return Result.Fail<int>(CustomerErrors.HoldingNotEditable);

        // PendingSync is the one live state a clause may not change in: the request is already in
        // a queue, and a clause changing underneath it would be sent as neither version.
        if (Status is HoldingStatus.PendingSync)
            return Result.Fail<int>(CustomerErrors.HoldingSyncInProgress);

        var validated = Validate(requested, roles);
        if (validated.IsFailure)
            return Result.Fail<int>(validated.Error!);

        // A NEW version only once the contract is live. Before that the clause is still being
        // captured and versioning every keystroke would bury the real avenants.
        var nextVersion = Status == HoldingStatus.Active ? ClauseVersion + 1 : ClauseVersion;

        foreach (var existing in _designations.Where(d => d.IsCurrent).ToList())
            existing.Close(nextVersion > ClauseVersion ? "SUPERSEDED" : "REPLACED", now);

        foreach (var request in requested)
        {
            _designations.Add(BeneficiaryDesignation.Create(
                tenantId:             TenantId,
                holdingId:            Id,
                roleCode:             request.RoleCode,
                subjectType:          request.SubjectType,
                subjectId:            request.SubjectId,
                externalFullName:     request.ExternalFullName,
                externalRelationship: request.ExternalRelationship,
                rank:                 request.Rank,
                sharePercent:         request.SharePercent,
                clauseVersion:        nextVersion,
                recordedBy:           recordedBy,
                now:                  now));
        }

        ClauseVersion = nextVersion;
        UpdatedAt = now;
        return Result.Ok(nextVersion);
    }

    /// <summary>
    /// Flags every current designation of a person who left the household (BNF-B-13), so the agent
    /// decides rather than the system.
    /// </summary>
    /// <returns>How many designations now need review.</returns>
    public int FlagDesignationsOf(Guid subjectId, string reason)
    {
        var affected = _designations
            .Where(d => d.IsCurrent
                     && d.SubjectId == subjectId
                     && d.Status == DesignationStatus.Active)
            .ToList();

        foreach (var designation in affected)
            designation.FlagForReview(reason);

        return affected.Count;
    }

    /// <summary>
    /// The cardinality and share rules of BNF-B-15, in one place.
    /// </summary>
    private static Result Validate(
        IReadOnlyList<DesignationRequest> requested, IReadOnlyList<DesignationRoleDto> roles)
    {
        foreach (var request in requested)
        {
            var role = roles.FirstOrDefault(r =>
                string.Equals(r.Code, request.RoleCode, StringComparison.OrdinalIgnoreCase));

            // A role the line does not accept. Checked per request rather than once over the set,
            // so the error names the offending role.
            if (role is null)
                return Result.Fail(CustomerErrors.DesignationRoleNotAllowed);

            if (request.Rank < 1)
                return Result.Fail(CustomerErrors.DesignationInvalidRank);

            if (request.SharePercent is < 0 or > 100)
                return Result.Fail(CustomerErrors.DesignationInvalidShare);

            // An external person is the only subject with no id, and the only one that needs a
            // name. Both halves, because each on its own is an unusable row: an id-less subject
            // nobody can name, or a named one that silently shadows a real client.
            if (request.SubjectType == DesignationSubjectType.ExternalPerson)
            {
                if (request.SubjectId is not null
                    || string.IsNullOrWhiteSpace(request.ExternalFullName))
                    return Result.Fail(CustomerErrors.DesignationSubjectInvalid);
            }
            else if (request.SubjectId is null || request.SubjectId == Guid.Empty)
            {
                // Guid.Empty is what a malformed JSON guid deserialises to in this API (see
                // GuidConverter), so it must never pass for a real subject.
                return Result.Fail(CustomerErrors.DesignationSubjectInvalid);
            }
        }

        // The same person twice in the same role and rank is a duplicate, not a bigger share.
        var keys = requested
            .Select(r => (r.RoleCode.Trim().ToUpperInvariant(), r.Rank,
                          r.SubjectId, r.ExternalFullName?.Trim().ToUpperInvariant()))
            .ToList();

        if (keys.Count != keys.Distinct().Count())
            return Result.Fail(CustomerErrors.DesignationDuplicateSubject);

        foreach (var role in roles)
        {
            var ofRole = requested
                .Where(r => string.Equals(r.RoleCode, role.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (ofRole.Count < role.MinCount)
                return Result.Fail(CustomerErrors.DesignationRequiredRoleMissing);

            if (role.MaxCount is { } max && ofRole.Count > max)
                return Result.Fail(CustomerErrors.DesignationTooMany);

            if (!role.SharesMustTotal100 || ofRole.Count == 0)
                continue;

            // PER RANK, never across (BNF-B-15): rank 2 inherits only if rank 1 cannot, so each
            // rank allocates the whole capital on its own.
            foreach (var rank in ofRole.Select(r => r.Rank).Distinct())
            {
                var total = ofRole.Where(r => r.Rank == rank).Sum(r => r.SharePercent ?? 0m);

                // Exactly 100, on a decimal, so 33.33 + 33.33 + 33.34 is the arithmetic an agent
                // actually types. No tolerance: a clause that does not add up is one an insurer
                // refuses, and rounding it here would hide that until the claim.
                if (total != 100m)
                    return Result.Fail(CustomerErrors.DesignationSharesMustTotal100);
            }
        }

        return Result.Ok();
    }
}

/// <summary>One designation as a caller submits it (BNF-B-15).</summary>
public sealed record DesignationRequest(
    string RoleCode,
    DesignationSubjectType SubjectType,
    Guid? SubjectId,
    string? ExternalFullName,
    string? ExternalRelationship,
    int Rank,
    decimal? SharePercent);
