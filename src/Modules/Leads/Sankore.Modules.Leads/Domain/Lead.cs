using Sankore.Modules.Leads.Domain.Events;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Money = Sankore.Shared.Kernel.ValueObject.Money;

namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Aggregate root for a prospect prior to conversion into a Customer (module M01).
/// Implements the full M13 lead lifecycle: Capture → Qualify → Assign → Convert.
/// Status tracks high-level lifecycle; PipelineStage tracks sales funnel position.
/// </summary>
public sealed class Lead : AggregateRoot
{
    // ── Identity ────────────────────────────────────────────────────────────

    public Guid Id { get; private set; }

    /// <summary>Full name (required). FirstName + LastName stored separately when available.</summary>
    public string FullName { get; private set; } = default!;
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public string PhoneNumber { get; private set; } = default!;
    /// <summary>HMAC-SHA256 blind index on the normalized phone (last 8 digits). Used for dedup queries.</summary>
    public string? PhoneBlindIndex { get; private set; }
    /// <summary>
    /// The prospect's e-mail, in CLEAR in the domain and ENCRYPTED in the column
    /// (<c>email_enc</c>, AES-256-GCM under M13's own key). The encryption is applied by the
    /// mapping, so no handler can forget it — see <c>LeadConfiguration</c>.
    ///
    /// <para>
    /// <b>Never compare this in a LINQ predicate.</b> The ciphertext is non-deterministic, so
    /// <c>l.Email == x</c> translates to a comparison of two different ciphertexts and matches
    /// nothing — silently, because it still compiles and still runs. Dedup by e-mail goes through
    /// <see cref="EmailBlindIndex"/>. In-memory comparison after materialisation is fine; that is
    /// what <c>IdentityMatchScorer</c> does.
    /// </para>
    /// </summary>
    public string? Email { get; private set; }

    /// <summary>
    /// HMAC-SHA256 blind index on the normalised e-mail — the second dedup key of F13.2 (BNF-B-01).
    /// Deterministic and indexed on <c>(tenant_id, email_blind_index)</c>, which is what makes an
    /// encrypted e-mail still searchable by exact match.
    ///
    /// <para>
    /// Moves as a pair with <see cref="Email"/>: both are written by <see cref="SetEmail"/> and by
    /// the capture factory, and by nothing else. Computed from M13's keyed <c>IBlindIndexer</c>
    /// with purpose <c>Email</c>, so it shares <c>Leads:BlindIndexKey</c> with the phone index —
    /// rotating that key invalidates both.
    /// </para>
    /// </summary>
    public string? EmailBlindIndex { get; private set; }

    /// <summary>
    /// TRANSITIONAL — the pre-encryption <c>email</c> column, read-only.
    ///
    /// <para>
    /// It exists only so that rows captured before the encryption migration keep matching while
    /// <c>BackfillLeadEmailProtectionJob</c> works through them: a dedup query falls back to this
    /// column when <see cref="EmailBlindIndex"/> is null, exactly as the phone dedup already falls
    /// back to a suffix match for leads with no <see cref="PhoneBlindIndex"/>. Without the
    /// fallback, every pre-migration lead would stop matching by e-mail the moment the new columns
    /// shipped — and nothing would say so.
    /// </para>
    ///
    /// <para>
    /// Removed, with the column and the fallback branches, once the backfill has reported zero
    /// remaining rows in every environment. Nothing may write it.
    /// </para>
    /// </summary>
    public string? LegacyEmailClear { get; private set; }

    public LeadGender Gender { get; private set; } = LeadGender.Unknown;
    public DateOnly? DateOfBirth { get; private set; }

    /// <summary>Individual (B2C) or Corporate (B2B) prospect.</summary>
    public LeadType ProspectType { get; private set; } = LeadType.Individual;

    // ── Organisation (optional, for corporate leads) ────────────────────────
    public string? CompanyName { get; private set; }
    public string? CompanyEmail { get; private set; }
    public string? CompanyPhone { get; private set; }
    public string? Website { get; private set; }

    // ── Capture context ──────────────────────────────────────────────────────
    public LeadStatus Status { get; private set; }
    public PipelineStage PipelineStage { get; private set; }
    public LeadSource Source { get; private set; }

    /// <summary>
    /// The configured <c>LeadSourceConfig</c> this lead was ingested through, or null when there
    /// is none — a lead typed into the UI, imported from a file, or produced by a merge never
    /// arrived through a configured source, and null says exactly that.
    ///
    /// Distinct from <see cref="Source"/> and not derivable from it: that enum is a coarse
    /// reporting axis (<c>LeadSource</c>), while a source config carries a different one
    /// (<c>LeadChannelType</c>) plus the dispatching rule, cost-per-lead and dedup window that
    /// actually govern this lead. Before this column existed the link lived only on
    /// <c>LeadIngestion.SourceId</c>, so every reader had to join through a side table.
    ///
    /// An OPAQUE reference: no physical foreign key, same as <c>LeadAssignment.RuleId</c>. A
    /// source that is later archived or deleted leaves a dangling id here, and readers must
    /// degrade gracefully rather than assume it resolves.
    ///
    /// Server-set only. It is never bound from an HTTP request body — see the note on
    /// <c>CaptureLeadCommand.LeadSourceConfigId</c> for why.
    /// </summary>
    public Guid? LeadSourceConfigId { get; private set; }

    public LeadChannel? Channel { get; private set; }
    public string? Campaign { get; private set; }
    public string? ExternalReference { get; private set; }
    public string? NationalId { get; private set; }
    public string? CustomerReference { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset CapturedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    // ── Household and PEP (BNF-B-03, BNF-B-05) ───────────────────────────────

    /// <summary>
    /// The principal's own PEP status (BNF-B-05). Never null — <see cref="PepStatus.NotExposed"/>
    /// until somebody declares otherwise, so "not asked" and "not exposed" do not share a
    /// representation that an LBC/FT file then cannot tell apart.
    /// </summary>
    public PepStatus PrincipalPep { get; private set; } = PepStatus.NotExposed;

    private readonly List<LeadHouseholdMember> _householdMembers = [];

    /// <summary>
    /// The spouse and children of the principal (BNF-B-03). Includes REMOVED members — read
    /// <see cref="ActiveHouseholdMembers"/> for the household as it stands.
    /// </summary>
    public IReadOnlyList<LeadHouseholdMember> HouseholdMembers => _householdMembers.AsReadOnly();

    public IEnumerable<LeadHouseholdMember> ActiveHouseholdMembers
        => _householdMembers.Where(m => m.IsActive);

    /// <summary>
    /// When somebody last DECLARED the principal's PEP status — exposed or not (BNF-B-05).
    ///
    /// <para>
    /// This column is what separates "not exposed" from "never asked". <see cref="PrincipalPep"/>
    /// defaults to <see cref="PepStatus.NotExposed"/> because an owned type cannot be absent, which
    /// means the status alone cannot carry the difference — and an LBC/FT file that cannot tell a
    /// negative answer from an unasked question has no answer at all. Null here means unasked, and
    /// the completeness check reports <c>PEP_NOT_DECLARED</c> on it.
    /// </para>
    ///
    /// <para>
    /// Set by <see cref="SetPrincipalPepStatus"/> and by nothing else. Existing rows are null, which
    /// is correct rather than a backfill gap: nobody declared anything about them.
    /// </para>
    /// </summary>
    public DateTimeOffset? PepDeclaredAt { get; private set; }

    // ── The principal's postal identity (BNF-B-02) ───────────────────────────
    //
    // Five flat columns rather than an owned PersonIdentity, for the reason given on
    // PrincipalIdentity below: FirstName, LastName, DateOfBirth and Gender are already columns that
    // the importers, the dedup scorer and every read-side projection depend on. These complete the
    // set so the projection can be a WHOLE person, which is what a household member inheriting the
    // principal's address needs — WithAddressOf had nothing to copy before.

    /// <summary>Name the principal is known by when it differs from <see cref="LastName"/>.</summary>
    public string? PrincipalUsageName { get; private set; }

    /// <summary>
    /// SENSITIVE — encrypted at rest, like every other postal address in the platform.
    ///
    /// <para>
    /// Nothing may compare this column in SQL: the ciphertext is non-deterministic, so an equality
    /// test matches nothing. Same rule as <see cref="Email"/>, and unlike it there is no blind
    /// index, because no feature searches leads by address.
    /// </para>
    /// </summary>
    public string? PrincipalAddressLine { get; private set; }

    public string? PrincipalPostalCode { get; private set; }
    public string? PrincipalCity { get; private set; }

    /// <summary>ISO 3166-1 alpha-2, upper-cased. See <c>IsoCountry</c>.</summary>
    public string? PrincipalCountryCode { get; private set; }

    /// <summary>
    /// The principal's identity as a value object, or null while the structured names are missing.
    ///
    /// <para>
    /// A PROJECTION over the flat columns, not a stored owned type: <see cref="FirstName"/>,
    /// <see cref="LastName"/> and <see cref="DateOfBirth"/> are already columns that the importers,
    /// the dedup scorer and every read-side projection depend on, and converting them to an owned
    /// type would be a migration across all of that for no behavioural gain. The household members
    /// DO store a <c>PersonIdentity</c>, because they are new.
    /// </para>
    ///
    /// <para>
    /// Null when either name is missing — BNF-B-01 makes every identity field but the phone
    /// optional at capture, and <c>PersonIdentity</c> requires both names. A caller resolving a
    /// child's inherited address therefore has to cope with a principal who has none yet.
    /// </para>
    ///
    /// <para>
    /// <b>One field is protected differently here than everywhere else.</b> <c>PersonIdentity</c>
    /// documents <c>BirthDate</c> as encrypted at rest, and a household member's is — but the
    /// principal's is <see cref="DateOfBirth"/>, a CLEAR column that predates this evolution and
    /// that the spreadsheet importers, the duplicate scorer and several read projections already
    /// read. Encrypting it is a migration across all of them, so it is deliberately out of scope
    /// and named here rather than left for a reader to discover. <see cref="PrincipalAddressLine"/>
    /// is new, so it IS encrypted.
    /// </para>
    /// </summary>
    public PersonIdentity? PrincipalIdentity
        => string.IsNullOrWhiteSpace(FirstName) || string.IsNullOrWhiteSpace(LastName)
            ? null
            : PersonIdentity.Create(
                lastName:    LastName,
                firstName:   FirstName,
                usageName:   PrincipalUsageName,
                birthDate:   DateOfBirth,
                gender: Gender switch
                {
                    LeadGender.Male   => PersonGender.Male,
                    LeadGender.Female => PersonGender.Female,
                    LeadGender.Other  => PersonGender.Other,
                    _                 => PersonGender.Unknown,
                },
                addressLine: PrincipalAddressLine,
                postalCode:  PrincipalPostalCode,
                city:        PrincipalCity,
                countryCode: PrincipalCountryCode);

    /// <summary>
    /// Whether anyone in this household is politically exposed (BNF-B-05) — the flag dispatching
    /// and the KYC vigilance level read.
    ///
    /// <para>
    /// STORED and not computed on read, deliberately. It is a column so dispatching can filter on
    /// it in SQL and so a change can raise <c>LeadPepExposureChanged</c> at the moment it happens;
    /// a computed property would need every reader to know the tenant's post-mandate window, and
    /// they would not all agree. <see cref="RecalculateHasPepExposure"/> is the only writer.
    /// </para>
    /// </summary>
    public bool HasPepExposure { get; private set; }

    // ── Existing-customer link (BNF-B-22) ────────────────────────────────────

    /// <summary>
    /// The M01 client this lead belongs to, when it is a known customer asking for another product
    /// (BNF-B-22). Null for a new prospect.
    ///
    /// <para>
    /// An OPAQUE reference with no foreign key — the client lives in another schema — and
    /// deliberately NOT the same field as <see cref="ConvertedToCustomerId"/>: this one says
    /// "whose request this is", set at capture or by an agent, and it is an INPUT to the
    /// conversion; that one says "what the conversion produced", and is an output. A lead can
    /// carry this and never convert.
    /// </para>
    ///
    /// <para>
    /// Written only by <see cref="AttachToCustomer"/> and <see cref="DetachFromCustomer"/>, both of
    /// which require a reason, because an attachment decides which client record the whole request
    /// reads from (BNF-B-23) and a silent one is unreviewable.
    /// </para>
    /// </summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>
    /// Why the current attachment state was chosen. Carried into the audit trail; null for a lead
    /// that was never attached.
    /// </summary>
    public string? CustomerLinkReason { get; private set; }

    /// <summary>
    /// Whether the attachment was decided by the matcher or by a person. An automatic link is only
    /// ever made on a unique, exact match (BNF-B-22); a partial or multiple match is a suggestion
    /// an agent confirms, and the two must stay distinguishable afterwards.
    /// </summary>
    public bool CustomerLinkIsAutomatic { get; private set; }

    public DateTimeOffset? CustomerLinkedAt { get; private set; }

    /// <summary>Derived from <see cref="CustomerId"/> — see <see cref="LeadKind"/>. Not a column.</summary>
    public LeadKind Kind => CustomerId.HasValue ? LeadKind.ExistingCustomer : LeadKind.NewProspect;

    // ── Product & qualification ──────────────────────────────────────────────

    /// <summary>
    /// The product line this request is about: the id of an M12 <c>ProductSpeciality</c>
    /// (BNF-B-01). Null until somebody resolves it, and null says exactly that.
    ///
    /// <para>
    /// It does NOT replace <see cref="InterestedProduct"/>, which stays as captured. That field
    /// already links a lead to a product, but by FREE-TEXT LABEL — a required 100-character string
    /// validated by a single <c>NotEmpty()</c>, holding values like "Crédit commerçant" and
    /// defaulted to "Unknown" by the pull job and the system capture endpoint. A label cannot carry
    /// the beneficiary policy, the household toggle, the required-fields list or the KYC tier,
    /// because those are all reads on a catalogue row. So: what the prospect said, and what an
    /// agent resolved it to. The same pair as <see cref="Source"/> and
    /// <see cref="LeadSourceConfigId"/>, for the same reason.
    /// </para>
    ///
    /// <para>
    /// An OPAQUE reference with no foreign key — another schema — so a retired product line leaves
    /// a resolvable-looking id and every reader must degrade rather than assume.
    /// </para>
    ///
    /// <para>
    /// <b>Server-set only</b>, and absent from <c>CaptureLeadRequest</c> /
    /// <c>SystemCaptureLeadRequest</c> on purpose. Nothing validates an id arriving from a public
    /// ingest, so a caller able to set it would choose the policy governing its own lead — which
    /// fields gate its conversion, the KYC tier required, and whether multiple holdings are
    /// allowed. Exactly the reasoning that keeps <see cref="LeadSourceConfigId"/> off those
    /// contracts.
    /// </para>
    /// </summary>
    public Guid? ProductLineId { get; private set; }

    public string InterestedProduct { get; private set; } = default!;
    public Money? DesiredAmount { get; private set; }
    public string PreferredLanguage { get; private set; } = default!;
    public double QualificationCompleteness { get; private set; }

    // ── Scoring & intent ─────────────────────────────────────────────────────
    public int Score { get; private set; }
    public LeadIntentLevel IntentLevel { get; private set; } = LeadIntentLevel.Unknown;

    // ── Location & preferences ───────────────────────────────────────────────
    public GeoPoint? Location { get; private set; }
    public Guid? PreferredAgencyId { get; private set; }

    // ── Ownership & assignment ───────────────────────────────────────────────
    //
    // Three ids that answer three different questions, and whose names do not make that obvious:
    //   OwnerId             → who is responsible for the relationship (a user)
    //   CurrentAssignmentId → which dispatch record is in force      (a LeadAssignment row)
    //   CurrentAssignedId   → who that record names                 (a user, copied from it)
    //
    // Every combination is legal and meaningful: an owner with no assignment (captured with an
    // owner, never dispatched), an assignment with no owner (routed by the engine, nobody named
    // responsible), or both pointing at different people (a manager owns the relationship, an
    // agent works the current task).

    /// <summary>
    /// Lead Owner — the commercial responsible for the relationship. A management decision, not a
    /// routing one: it comes from the capture (an import column, a counter entry) or from
    /// <see cref="SetOwner"/> via <c>UpdateLeadOwnerCommand</c> / <c>BulkAssignOwnerCommand</c>,
    /// every change is mirrored into <see cref="LeadOwnerAssignmentHistory"/>, and
    /// <c>ListLeads</c> filters on it.
    ///
    /// <para>
    /// Dispatching NEVER writes this — deliberately. <c>LeadAutoDispatchConsumer</c> skips a lead
    /// whose capture already named an owner (<c>LeadCapturedEvent.HasExplicitOwner</c>), because
    /// auto-dispatch must not overrule a human decision and, after the fact, nothing can tell
    /// whether this field came from the capture or from a later assignment.
    /// </para>
    /// </summary>
    public Guid? OwnerId { get; private set; }

    public Guid? AgencyId { get; private set; }

    /// <summary>
    /// The agent named by <see cref="CurrentAssignmentId"/>'s row — a denormalised copy of
    /// <c>LeadAssignment.AgentId</c>, so the read side never has to join: the <c>ListLeads</c>
    /// projection, <c>GetAgentPerformance</c>'s grouping and <c>LeadsModuleFacade</c> all read it
    /// directly. A user id of the Administration module, hence no foreign key — another schema.
    ///
    /// <para>
    /// It is NOT the owner, and one letter is all that separates it from
    /// <see cref="CurrentAssignmentId"/> while the two point at different tables; both being
    /// <c>Guid?</c>, reading one for the other compiles. It moves as a pair with that field —
    /// <see cref="AssignTo"/> sets both, <see cref="ReturnToQueue"/> clears both, and nothing else
    /// may touch either.
    /// </para>
    /// </summary>
    public Guid? CurrentAssignedId { get; private set; }

    /// <summary>
    /// Capture-only provenance: the field agent who collected this lead. Never updated afterwards,
    /// and unrelated to ownership or to dispatching.
    /// </summary>
    public Guid? AgentCollectedLeadId { get; private set; }

    /// <summary>
    /// The <see cref="LeadAssignment"/> currently in force — the work order rather than the person:
    /// the strategy that picked the agent, the compatibility score and its factors, the rule that
    /// produced it, the SLA deadline, the first contact, and whether a supervisor overrode the
    /// engine. <c>RecordFirstContactHandler</c> and <c>GetNextActionHandler</c> join through it for
    /// exactly those.
    ///
    /// <para>
    /// Null means "not currently assigned", which is also what puts the lead back in the dispatch
    /// queue (<see cref="ReturnToQueue"/>). Non-null is the idempotency key of auto-dispatch: a
    /// replayed <c>LeadCapturedEvent</c> finds it set and the consumer stops, which is exact
    /// because the assignment and the lead are written in the same transaction.
    /// </para>
    ///
    /// <para>
    /// Only the CURRENT one. Previous assignments are not lost — they remain as
    /// <see cref="LeadAssignment"/> rows, which is what the assignment history reads.
    /// </para>
    /// </summary>
    public Guid? CurrentAssignmentId { get; private set; }

    /// <summary>
    /// When the lead entered the dispatch queue: a dispatch found no agent for it, and
    /// <c>DispatchWaitingLeadsJob</c> retries it under the dispatching rule until one becomes
    /// available. Null when the lead is not waiting — never dispatched, or assigned since.
    ///
    /// <para>
    /// Before the queue, a failed dispatch was final: the lead stayed unassigned until somebody
    /// noticed it in the list, and an import that ran while every agent was busy left all its
    /// leads unrouted for good.
    /// </para>
    /// </summary>
    public DateTimeOffset? AwaitingDispatchSince { get; private set; }

    /// <summary>
    /// Puts the lead in the dispatch queue, and says whether it was not already there — the caller
    /// announces only that first entry, so a retry that fails again stays silent. A lead that still
    /// has an agent is not queued: a failed REassignment leaves it with the one it has.
    /// </summary>
    public bool MarkAwaitingDispatch(DateTimeOffset now)
    {
        if (AwaitingDispatchSince is not null || CurrentAssignmentId is not null || !IsDispatchable)
            return false;

        AwaitingDispatchSince = now;
        return true;
    }

    // ── Attribution & UTM (F13.37-BE-04) ───────────────────────────────────
    public string? UtmSource { get; private set; }
    public string? UtmMedium { get; private set; }
    public string? UtmCampaign { get; private set; }
    public string? UtmContent { get; private set; }
    public string? UtmTerm { get; private set; }
    public string? LandingPage { get; private set; }
    public string? Referrer { get; private set; }
    public string? ExternalId { get; private set; }
    public string? SocialPublicationId { get; private set; }
    public string? SocialInteractionId { get; private set; }

    /// <summary>Snapshot of the source's CostPerLead at capture time. Never recalculated.</summary>
    public Money? AcquisitionCost { get; private set; }

    /// <summary>True for leads captured from a source in Testing status. Not dispatched.</summary>
    public bool IsTest { get; private set; }

    // ── Lifecycle tracking ───────────────────────────────────────────────────
    public DateTimeOffset? LastActivityAt { get; private set; }
    public string? LossReason { get; private set; }
    public DateTimeOffset? ConvertedAt { get; private set; }
    public Guid? ConvertedToCustomerId { get; private set; }

    private Lead() { } // EF Core

    // ── Factory ─────────────────────────────────────────────────────────────

    public static Lead Capture(
        Guid tenantId,
        string fullName,
        string phoneNumber,
        LeadSource source,
        string interestedProduct,
        string preferredLanguage,
        GeoPoint location,
        Guid? preferredAgencyId,
        TimeProvider clock,
        TimeSpan? lifetime = null,
        string? firstName = null,
        string? lastName = null,
        string? email = null,
        LeadGender gender = LeadGender.Unknown,
        DateOnly? dateOfBirth = null,
        Money? desiredAmount = null,
        string? campaign = null,
        LeadChannel? channel = null,
        string? comment = null,
        string? externalReference = null,
        Guid? ownerId = null,
        Guid? agencyId = null,
        Guid? agentCollectedLeadId = null,
        LeadType prospectType = LeadType.Individual,
        string? nationalId = null,
        string? customerReference = null,
        string? phoneBlindIndex = null,
        string? utmSource = null,
        string? utmMedium = null,
        string? utmCampaign = null,
        string? utmContent = null,
        string? utmTerm = null,
        string? landingPage = null,
        string? referrer = null,
        string? externalId = null,
        string? socialPublicationId = null,
        string? socialInteractionId = null,
        Money? acquisitionCost = null,
        bool isTest = false,
        Guid? leadSourceConfigId = null,
        string? emailBlindIndex = null,
        Guid? customerId = null,
        Guid? productLineId = null,
        bool customerLinkIsAutomatic = false,
        string? customerLinkReason = null)
    {
        // Structured names win for a new lead (BNF-B-01): FullName is DERIVED when both are
        // present and read-only from then on. The passed fullName is the fallback for the capture
        // contracts that carry only it — the v1 ingest, a file import, a web form — and those
        // leads are never split automatically, which is why no branch here parses a full name.
        var resolvedFullName = ResolveFullName(fullName, firstName, lastName);

        if (string.IsNullOrWhiteSpace(resolvedFullName))
            throw new DomainException("Lead must have a name.");
        if (string.IsNullOrWhiteSpace(phoneNumber))
            throw new DomainException("Lead must have a phone number.");

        var now = clock.GetUtcNow();

        var lead = new Lead
        {
            Id                    = Guid.NewGuid(),
            TenantId              = tenantId,
            FullName              = resolvedFullName,
            FirstName             = firstName?.Trim(),
            LastName              = lastName?.Trim(),
            PhoneNumber           = phoneNumber.Trim(),
            PhoneBlindIndex       = phoneBlindIndex,
            Email                 = Blank(email),
            EmailBlindIndex       = Blank(email) is null ? null : emailBlindIndex,
            Gender                = gender,
            DateOfBirth           = dateOfBirth,
            Source                = source,
            Channel               = channel,
            Campaign              = campaign?.Trim(),
            ExternalReference     = externalReference?.Trim(),
            Comment               = comment?.Trim(),
            InterestedProduct     = interestedProduct,
            DesiredAmount         = desiredAmount,
            PreferredLanguage     = preferredLanguage ?? "FR",
            Location              = location,
            PreferredAgencyId     = preferredAgencyId,
            AgencyId              = agencyId,
            OwnerId               = ownerId,
            AgentCollectedLeadId  = agentCollectedLeadId,
            ProspectType          = prospectType,
            NationalId            = nationalId?.Trim(),
            CustomerReference     = customerReference?.Trim(),
            Status                = LeadStatus.New,
            PipelineStage         = Domain.PipelineStage.New,
            IntentLevel           = LeadIntentLevel.Unknown,
            Score                 = 0,
            QualificationCompleteness = 0,
            CapturedAt            = now,
            CreatedAt             = now,
            UpdatedAt             = now,
            ExpiresAt             = now.Add(lifetime ?? TimeSpan.FromDays(180)),
            Website = "",
            CompanyEmail = "",
            CompanyName = "",
            CompanyPhone = "",
            UtmSource             = utmSource?.Trim(),
            UtmMedium             = utmMedium?.Trim(),
            UtmCampaign           = utmCampaign?.Trim(),
            UtmContent            = utmContent?.Trim(),
            UtmTerm               = utmTerm?.Trim(),
            LandingPage           = landingPage?.Trim(),
            Referrer              = referrer?.Trim(),
            ExternalId            = externalId?.Trim(),
            SocialPublicationId   = socialPublicationId?.Trim(),
            SocialInteractionId   = socialInteractionId?.Trim(),
            AcquisitionCost       = acquisitionCost,
            IsTest                = isTest,
            LeadSourceConfigId    = leadSourceConfigId,
            ProductLineId         = productLineId,
            CustomerId            = customerId,
            CustomerLinkIsAutomatic = customerId.HasValue && customerLinkIsAutomatic,
            CustomerLinkReason    = customerId.HasValue ? Blank(customerLinkReason) : null,
            CustomerLinkedAt      = customerId.HasValue ? now : null,
        };

        lead.RaiseDomainEvent(new LeadCapturedDomainEvent(lead.Id));

        if (customerId is { } linkedCustomerId)
        {
            lead.RaiseDomainEvent(new LeadLinkedToCustomerDomainEvent(
                lead.Id, linkedCustomerId, lead.CustomerLinkIsAutomatic));
        }

        return lead;
    }

    /// <summary>
    /// The one place that decides what <see cref="FullName"/> holds. Both names present wins;
    /// otherwise an explicitly supplied full name; otherwise whichever single name was given.
    /// Never the reverse — a full name is never SPLIT into first and last (BNF-B-01), because
    /// "Kouassi Aya N'Guessan" has no reading a machine can defend.
    /// </summary>
    private static string ResolveFullName(string? fullName, string? firstName, string? lastName)
    {
        var first = Blank(firstName);
        var last  = Blank(lastName);

        if (first is not null && last is not null)
            return $"{first} {last}";

        var explicitName = Blank(fullName);
        if (explicitName is not null)
            return explicitName;

        return first ?? last ?? string.Empty;
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Historical qualification threshold; see <see cref="Qualify"/>.</summary>
    public const int DefaultQualifiedThreshold = 60;

    /// <summary>
    /// A lead can be dispatched unless it has reached a terminal status. One predicate, so the
    /// domain and <c>DispatchLeadHandler</c> cannot disagree on what "dispatchable" means.
    /// </summary>
    public bool IsDispatchable =>
        Status is not (LeadStatus.Converted or LeadStatus.Lost
                    or LeadStatus.Disqualified or LeadStatus.Archived);

    // ── Status transitions ──────────────────────────────────────────────────

    /// <summary>Moves Lead from New to Open (first manual or system action).</summary>
    public Result Open()
    {
        if (Status != LeadStatus.New)
            return Result.Fail("Only a New lead can be opened.");

        Status    = LeadStatus.Open;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, LeadStatus.New, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Qualifies the lead with a 0-100 score.
    /// Leads scoring ≥ 60 become Qualified (eligible for dispatching);
    /// below that they enter Qualifying for further work.
    /// </summary>
    /// <param name="qualifiedThreshold">
    /// Score at or above which the lead becomes <see cref="LeadStatus.Qualified"/> rather than
    /// <see cref="LeadStatus.Qualifying"/>. Configurable because 60 was calibrated for
    /// qualification AFTER interactions: <c>LeadScoreCalculator</c> awards 35 of its 100 points
    /// from activity history, so a lead scored at capture can never reach 60 (45 at most when it
    /// came from a file import, whose source quality is 5/20). A tenant that wants captured leads
    /// dispatched lowers this; everyone else keeps the historical 60.
    /// </param>
    public Result Qualify(int score, int qualifiedThreshold = DefaultQualifiedThreshold)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived or LeadStatus.Lost or LeadStatus.Disqualified)
            return Result.Fail("LEAD_CANNOT_BE_QUALIFIED_FROM_CURRENT_STATUS");

        if (score is < 0 or > 100)
            return Result.Fail("SCORE_OUT_OF_RANGE");

        Score     = score;
        Status    = score >= qualifiedThreshold ? LeadStatus.Qualified : LeadStatus.Qualifying;
        UpdatedAt = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadQualifiedDomainEvent(Id, Status, score));
        return Result.Ok();
    }

    /// <summary>
    /// Records a dispatching assignment. Any lead that is still live may be dispatched — a
    /// captured lead is routed to an agent precisely so that someone qualifies it. Only the
    /// terminal statuses refuse: there is nobody to work a lead that is already converted, lost,
    /// disqualified or archived.
    /// </summary>
    /// <param name="currentAssignment">
    /// The assignment this one replaces, loaded by the caller, or <c>null</c> when the lead has
    /// none. It is a REQUIRED parameter rather than an optional one, and a mismatch is refused:
    /// the row has to be closed here, and this aggregate holds no navigation to reach it by
    /// itself. See <see cref="CloseCurrentAssignment"/> for what goes wrong when it is not.
    /// </param>
    public Result AssignTo(LeadAssignment assignment, LeadAssignment? currentAssignment)
    {
        if (!IsDispatchable)
            return Result.Fail("LEAD_NOT_DISPATCHABLE");

        var closing = CloseCurrentAssignment(currentAssignment);
        if (closing.IsFailure)
            return closing;

        CurrentAssignmentId   = assignment.Id;
        CurrentAssignedId     = assignment.AgentId;
        // Whoever placed it — the queue, the engine at capture, a branch manager by hand — the
        // lead has an agent and leaves the queue.
        AwaitingDispatchSince = null;
        UpdatedAt             = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadAssignedDomainEvent(Id, assignment.Id, assignment.AgentId));
        return Result.Ok();
    }

    /// <summary>Reverts to Qualified so the lead re-enters the dispatching queue.</summary>
    /// <param name="currentAssignment">
    /// The assignment being given up, loaded by the caller. Required for the same reason as in
    /// <see cref="AssignTo"/>: leaving it open is what used to keep alerting its agent.
    /// </param>
    public Result ReturnToQueue(LeadAssignment? currentAssignment)
    {
        if (CurrentAssignmentId is null)
            return Result.Fail("LEAD_IS_NOT_CURRENTLY_ASSIGNED");

        var closing = CloseCurrentAssignment(currentAssignment);
        if (closing.IsFailure)
            return closing;

        CurrentAssignmentId = null;
        CurrentAssignedId   = null;
        Status              = LeadStatus.Qualified;
        UpdatedAt           = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    /// <summary>
    /// Stamps the outgoing assignment as superseded, so exactly one row of a lead is ever open.
    ///
    /// <para>
    /// The caller has to hand the row over because <c>LeadAssignment</c> is mapped with
    /// <c>HasOne&lt;Lead&gt;().WithMany()</c> and no navigation property: this aggregate can see
    /// the id of its current assignment and nothing else. Rather than let a caller silently skip
    /// the step — which is precisely the bug this closes, a replaced row alerting its former agent
    /// every day through <c>CheckSlaBreachesJob</c> — a lead that HAS a current assignment and is
    /// handed <c>null</c> is refused with <c>CURRENT_ASSIGNMENT_REQUIRED</c>, and a row that is not
    /// the current one with <c>CURRENT_ASSIGNMENT_MISMATCH</c>.
    /// </para>
    /// </summary>
    private Result CloseCurrentAssignment(LeadAssignment? currentAssignment)
    {
        if (CurrentAssignmentId is null)
            return Result.Ok();

        if (currentAssignment is null)
            return Result.Fail("CURRENT_ASSIGNMENT_REQUIRED");

        if (currentAssignment.Id != CurrentAssignmentId.Value)
            return Result.Fail("CURRENT_ASSIGNMENT_MISMATCH");

        currentAssignment.Supersede(DateTimeOffset.UtcNow);
        return Result.Ok();
    }

    /// <summary>Sets the Lead Owner (commercial responsible).</summary>
    public Result SetOwner(Guid ownerId)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot change owner of a closed lead.");

        var previous = OwnerId;
        OwnerId   = ownerId;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadOwnerChangedDomainEvent(Id, previous, ownerId));
        return Result.Ok();
    }

    /// <summary>Advances the pipeline stage, enforcing the allow-list of forward transitions.</summary>
    public Result AdvancePipelineStage(PipelineStage newStage)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived)
            return Result.Fail("Cannot move pipeline stage on a closed lead.");

        var previous  = PipelineStage;
        PipelineStage = newStage;
        UpdatedAt     = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadPipelineStageChangedDomainEvent(Id, previous, newStage));
        return Result.Ok();
    }

    /// <summary>
    /// Updates mutable lead information fields.
    /// </summary>
    /// <param name="fullName">
    /// Applied only while the lead has no structured first/last pair — see BNF-B-01 and the
    /// derivation below. A caller that wants to rename a structured lead sets the two names.
    /// </param>
    /// <remarks>
    /// There is NO e-mail parameter, deliberately. An e-mail and its blind index must be written
    /// together or dedup goes quietly blind, so the pair has exactly one setter —
    /// <see cref="SetEmail"/>, which takes the index with it. Leaving an ignored parameter here
    /// would have let a caller keep passing an e-mail that silently went nowhere; removing it makes
    /// the compiler point at every call site instead.
    /// </remarks>
    public Result Update(
        string? fullName,
        string? firstName,
        string? lastName,
        LeadGender? gender,
        DateOnly? dateOfBirth,
        string? interestedProduct,
        Money? desiredAmount,
        string? preferredLanguage,
        string? campaign,
        string? comment,
        GeoPoint? location,
        Guid? preferredAgencyId)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");

        if (fullName is not null)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return Result.Fail("Full name cannot be blank.");
            FullName = fullName.Trim();
        }

        if (firstName is not null) FirstName = Blank(firstName);
        if (lastName  is not null) LastName  = Blank(lastName);
        if (gender    is not null) Gender    = gender.Value;

        // Structured names win (BNF-B-01). Once a lead has both, FullName is derived and the
        // fullName argument above no longer decides anything — renaming means changing FirstName
        // or LastName. Applied AFTER the two so a request that changes one of them is reflected.
        if (FirstName is not null && LastName is not null)
            FullName = $"{FirstName} {LastName}";
        if (dateOfBirth      is not null) DateOfBirth      = dateOfBirth;
        if (interestedProduct is not null) InterestedProduct = interestedProduct;
        if (desiredAmount    is not null) DesiredAmount    = desiredAmount;
        if (preferredLanguage is not null) PreferredLanguage = preferredLanguage;
        if (campaign  is not null) Campaign  = campaign.Trim();
        if (comment   is not null) Comment   = comment.Trim();
        if (location  is not null) Location  = location;
        if (preferredAgencyId is not null) PreferredAgencyId = preferredAgencyId;

        UpdatedAt = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    /// <summary>
    /// Updates the score in-place without changing lead status.
    /// Used for background auto-recalculations triggered by activity or data changes.
    /// </summary>
    internal void RecordScoreUpdate(int score)
    {
        Score     = Math.Clamp(score, 0, 100);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Updates the intent level (Hot/Warm/Cold/Unknown).</summary>
    public void UpdateIntentLevel(LeadIntentLevel level)
    {
        IntentLevel = level;
        UpdatedAt   = DateTimeOffset.UtcNow;
    }

    /// <summary>Records that an activity has been performed, refreshing LastActivityAt.</summary>
    public void RecordActivity()
    {
        LastActivityAt = DateTimeOffset.UtcNow;
        UpdatedAt      = DateTimeOffset.UtcNow;
    }

    /// <summary>Updates the qualification completeness percentage (0–1).</summary>
    public void SetQualificationCompleteness(double completeness)
    {
        QualificationCompleteness = Math.Clamp(completeness, 0, 1);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Closes the lead as Lost, Disqualified, or Archived.</summary>
    public Result Close(LeadCloseReason reason, string? detail = null)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived or LeadStatus.Lost or LeadStatus.Disqualified)
            return Result.Fail("Lead is already closed.");

        var previous = Status;
        Status = reason switch
        {
            LeadCloseReason.Lost         => LeadStatus.Lost,
            LeadCloseReason.Disqualified => LeadStatus.Disqualified,
            LeadCloseReason.Archived     => LeadStatus.Archived,
            _                            => LeadStatus.Lost
        };

        LossReason = detail;
        UpdatedAt  = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Marks the lead as converted to a customer.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent, and it has to be</b> (BNF-B-24). A conversion is reachable from a retried
    /// request and from an agent double-clicking; before this guard a second call re-stamped
    /// <see cref="ConvertedAt"/> and raised <c>LeadConverted</c> again, so a back office would be
    /// told twice to open the same holding. Converting to the SAME customer therefore succeeds and
    /// changes nothing; converting to a DIFFERENT one is refused, because a lead that produced one
    /// client cannot be made to have produced another.
    /// </remarks>
    /// <returns><c>LEAD_ALREADY_CONVERTED_TO_ANOTHER_CUSTOMER</c>, or the closed-lead refusal.</returns>
    public Result Convert(Guid customerId)
    {
        if (Status is LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived)
            return Result.Fail("Cannot convert a closed lead.");

        if (Status == LeadStatus.Converted)
        {
            return ConvertedToCustomerId == customerId
                ? Result.Ok()
                : Result.Fail("LEAD_ALREADY_CONVERTED_TO_ANOTHER_CUSTOMER");
        }

        var previous              = Status;
        Status                    = LeadStatus.Converted;
        PipelineStage             = Domain.PipelineStage.Converted;
        ConvertedAt               = DateTimeOffset.UtcNow;
        ConvertedToCustomerId     = customerId;
        UpdatedAt                 = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        RaiseDomainEvent(new LeadConvertedDomainEvent(Id, customerId));
        return Result.Ok();
    }

    /// <summary>Legacy overload kept for DispatchLead backward compatibility.</summary>
    public Result Convert()
    {
        if (Status is LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived)
            return Result.Fail("ONLY_ASSIGNED_LEADS_CAN_BE_CONVERTED");

        var previous  = Status;
        Status        = LeadStatus.Converted;
        PipelineStage = Domain.PipelineStage.Converted;
        ConvertedAt   = DateTimeOffset.UtcNow;
        UpdatedAt     = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>Marks the lead as Lost (legacy helper, delegates to Close).</summary>
    public Result MarkLost(string reason) => Close(LeadCloseReason.Lost, reason);

    // ── E-mail (encrypted + blind index) ─────────────────────────────────────

    /// <summary>
    /// Sets or clears the e-mail together with its blind index (BNF-B-01).
    ///
    /// <para>
    /// The ONE setter for the pair, deliberately. The column is encrypted and therefore
    /// uncomparable in SQL, so every exact-match query reads <see cref="EmailBlindIndex"/> — and
    /// an index left behind by a changed e-mail, or missing from a new one, makes those queries
    /// return the wrong rows or none, with nothing in the logs. Two fields that must agree get one
    /// door.
    /// </para>
    /// </summary>
    /// <param name="blindIndex">
    /// Computed by the caller from M13's keyed <c>IBlindIndexer</c> (purpose <c>Email</c>). Passed
    /// in rather than computed here because the aggregate has no services — the same reason
    /// <see cref="PhoneBlindIndex"/> arrives from the handler.
    /// </param>
    public Result SetEmail(string? email, string? blindIndex)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");

        var normalized = Blank(email);

        if (normalized is null)
        {
            Email           = null;
            EmailBlindIndex = null;
            UpdatedAt       = DateTimeOffset.UtcNow;
            return Result.Ok();
        }

        if (string.IsNullOrWhiteSpace(blindIndex))
            return Result.Fail("EMAIL_BLIND_INDEX_REQUIRED");

        Email           = normalized;
        EmailBlindIndex = blindIndex;
        UpdatedAt       = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    /// <summary>
    /// Fills the protected e-mail columns of a lead captured before the encryption migration, from
    /// <see cref="LegacyEmailClear"/>. Used ONLY by <c>BackfillLeadEmailProtectionJob</c>.
    ///
    /// <para>
    /// Internal, and it touches no timestamp: a backfill is not a change to the lead, and bumping
    /// <c>UpdatedAt</c> on every row would move every lead to the top of every
    /// recently-modified list the day it runs.
    /// </para>
    /// </summary>
    internal void BackfillEmailProtection(string blindIndex)
    {
        if (EmailBlindIndex is not null || LegacyEmailClear is null)
            return;

        Email           = LegacyEmailClear.Trim();
        EmailBlindIndex = blindIndex;
    }

    /// <summary>
    /// Drops a legacy e-mail value that normalises to nothing, so the backfill stops re-selecting
    /// the row. Used ONLY by <c>LeadEmailProtectionBackfill</c>.
    /// </summary>
    internal void ClearLegacyEmail() => LegacyEmailClear = null;

    // ── Household (BNF-B-03) ─────────────────────────────────────────────────

    /// <summary>
    /// Whether the household may be edited at all: a live lead that has reached an agent.
    ///
    /// <para>
    /// <b>There is no <c>LeadStatus.Assigned</c>.</b> BNF-B-03 asks for "statut Assigned (ou
    /// ultérieur non terminal)", but this module has never had such a status — assignment is
    /// <see cref="CurrentAssignmentId"/>, and the statuses are New / Open / Qualifying / Qualified
    /// / Nurturing / Recycled plus the four terminal ones. So the condition is expressed as what
    /// it actually means: the lead is not terminal AND somebody is working it.
    /// </para>
    ///
    /// <para>
    /// <see cref="OwnerId"/> counts as well as an assignment. A lead captured at a counter with an
    /// owner named and never routed through the engine has somebody responsible for it, and
    /// refusing to let that person record the household would make the walk-in path unusable.
    /// </para>
    /// </summary>
    public bool IsHouseholdEditable => IsDispatchable && (CurrentAssignmentId is not null || OwnerId is not null);

    /// <summary>
    /// Adds a spouse or a child (BNF-B-03).
    /// </summary>
    /// <param name="maxChildren">
    /// From the product line's beneficiary policy; null = no ceiling. Passed in rather than read
    /// here because the aggregate has no services.
    /// </param>
    /// <param name="childMaxAge">From the policy; null = no limit. Checked at <paramref name="now"/>.</param>
    /// <returns>
    /// <c>LEAD_HOUSEHOLD_NOT_EDITABLE</c>, <c>SPOUSE_UNION_TYPE_REQUIRED</c>,
    /// <c>UNION_TYPE_FORBIDDEN_FOR_CHILD</c>, <c>HOUSEHOLD_ALREADY_HAS_SPOUSE</c>,
    /// <c>HOUSEHOLD_MAX_CHILDREN_REACHED</c>, <c>HOUSEHOLD_CHILD_TOO_OLD</c> or
    /// <c>HOUSEHOLD_MEMBER_NEEDS_OWN_ADDRESS</c>.
    /// </returns>
    public Result<LeadHouseholdMember> AddHouseholdMember(
        HouseholdMemberType type,
        SpouseUnionType? spouseUnionType,
        PersonIdentity identity,
        PepStatus? pep,
        bool usesPrincipalAddress,
        DateTimeOffset now,
        int? maxChildren = null,
        int? childMaxAge = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!IsHouseholdEditable)
            return Result.Fail<LeadHouseholdMember>("LEAD_HOUSEHOLD_NOT_EDITABLE");

        if (type == HouseholdMemberType.Spouse && spouseUnionType is null)
            return Result.Fail<LeadHouseholdMember>("SPOUSE_UNION_TYPE_REQUIRED");
        if (type == HouseholdMemberType.Child && spouseUnionType is not null)
            return Result.Fail<LeadHouseholdMember>("UNION_TYPE_FORBIDDEN_FOR_CHILD");

        // At most ONE ACTIVE spouse. Doubled by the partial unique index uq_lead_one_spouse — the
        // aggregate's check gives a usable error code, the index is what holds under two concurrent
        // requests, which no in-memory check can.
        if (type == HouseholdMemberType.Spouse
            && ActiveHouseholdMembers.Any(m => m.Type == HouseholdMemberType.Spouse))
            return Result.Fail<LeadHouseholdMember>("HOUSEHOLD_ALREADY_HAS_SPOUSE");

        if (type == HouseholdMemberType.Child)
        {
            var activeChildren = ActiveHouseholdMembers.Count(m => m.Type == HouseholdMemberType.Child);
            if (maxChildren is { } ceiling && activeChildren >= ceiling)
                return Result.Fail<LeadHouseholdMember>("HOUSEHOLD_MAX_CHILDREN_REACHED");

            // Only when the age is KNOWN. A child with no birth date is not refused here —
            // BNF-B-01 makes identity fields optional at capture, and refusing the member would
            // lose the household rather than flag the gap. The completeness check is what reports
            // the missing date (BNF-B-04).
            if (childMaxAge is { } limit
                && identity.AgeAt(DateOnly.FromDateTime(now.UtcDateTime)) is { } age
                && age > limit)
                return Result.Fail<LeadHouseholdMember>("HOUSEHOLD_CHILD_TOO_OLD");
        }

        // A member that does not inherit the principal's address must carry its own: the whole
        // point of the flag is that exactly one of the two holds the postal fields.
        if (!usesPrincipalAddress && string.IsNullOrWhiteSpace(identity.AddressLine))
            return Result.Fail<LeadHouseholdMember>("HOUSEHOLD_MEMBER_NEEDS_OWN_ADDRESS");

        var stored = usesPrincipalAddress
            // Cleared on write, not merely ignored on read: a stale address left in the row is one
            // a later "stop inheriting" would silently resurrect.
            ? identity.WithoutPostalAddress()
            : identity;

        var member = LeadHouseholdMember.Create(
            TenantId, Id, type, spouseUnionType, stored,
            // BNF-B-05: a household of an exposed principal inherits FamilyMember by default. An
            // explicitly supplied status wins — that is the "modifiable avec motif" half.
            pep ?? PrincipalPep.AsFamilyMemberLink(),
            // DECLARED only when somebody actually answered about this member: the caller supplied
            // a status, or the principal's own exposure is what is being propagated. An unexposed
            // principal propagates "not exposed", which is a default and not an answer about the
            // spouse — stamping it would make the completeness check report a question nobody was
            // asked as answered (see PepDeclaredAt).
            pepDeclaredAt: pep is not null || PrincipalPep is { IsPep: true, Link: PepLink.Self }
                ? now
                : null,
            usesPrincipalAddress, now);

        _householdMembers.Add(member);
        UpdatedAt = now;

        RaiseDomainEvent(new LeadHouseholdChangedDomainEvent(Id, member.Id, type.ToString(), Added: true));
        return Result.Ok(member);
    }

    /// <summary>Removes a member logically (BNF-B-03) — never a delete.</summary>
    public Result RemoveHouseholdMember(Guid memberId, string reason, DateTimeOffset now)
    {
        if (!IsHouseholdEditable)
            return Result.Fail("LEAD_HOUSEHOLD_NOT_EDITABLE");

        var member = _householdMembers.FirstOrDefault(m => m.Id == memberId);
        if (member is null)
            return Result.Fail("HOUSEHOLD_MEMBER_NOT_FOUND");

        var removed = member.Remove(reason, now);
        if (removed.IsFailure)
            return removed;

        UpdatedAt = now;
        RaiseDomainEvent(new LeadHouseholdChangedDomainEvent(
            Id, member.Id, member.Type.ToString(), Added: false));
        return Result.Ok();
    }

    /// <summary>Updates a member's identity, union type and address inheritance.</summary>
    public Result UpdateHouseholdMember(
        Guid memberId,
        PersonIdentity identity,
        SpouseUnionType? spouseUnionType,
        bool usesPrincipalAddress,
        DateTimeOffset now,
        int? childMaxAge = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!IsHouseholdEditable)
            return Result.Fail("LEAD_HOUSEHOLD_NOT_EDITABLE");

        var member = _householdMembers.FirstOrDefault(m => m.Id == memberId);
        if (member is null)
            return Result.Fail("HOUSEHOLD_MEMBER_NOT_FOUND");

        if (!usesPrincipalAddress && string.IsNullOrWhiteSpace(identity.AddressLine))
            return Result.Fail("HOUSEHOLD_MEMBER_NEEDS_OWN_ADDRESS");

        if (member.Type == HouseholdMemberType.Child
            && childMaxAge is { } limit
            && identity.AgeAt(DateOnly.FromDateTime(now.UtcDateTime)) is { } age
            && age > limit)
            return Result.Fail("HOUSEHOLD_CHILD_TOO_OLD");

        var union = member.UpdateUnionType(spouseUnionType);
        if (union.IsFailure)
            return union;

        var stored = usesPrincipalAddress
            ? identity.WithoutPostalAddress()
            : identity;

        var updated = member.UpdateIdentity(stored, usesPrincipalAddress);
        if (updated.IsFailure)
            return updated;

        UpdatedAt = now;
        return Result.Ok();
    }

    // ── Anonymisation (BNF-B-19) ─────────────────────────────────────────────

    /// <summary>
    /// Whether this lead's personal data has already been destroyed (BNF-B-19). The row survives —
    /// what it says about the FUNNEL (a source, a score, a status, dates) is statistics the
    /// institution keeps, and what it said about a PERSON is gone.
    /// </summary>
    public bool IsAnonymized { get; private set; }

    public DateTimeOffset? AnonymizedAt { get; private set; }

    /// <summary>
    /// The moment the retention clock starts: when the lead reached a terminal state (BNF-B-19).
    /// Null for a live lead, which is never anonymised however old it is.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored, from whichever terminal timestamp applies. A `ClosedAt` column
    /// would be a fourth place recording the same event, and the three that exist are each written
    /// by a different transition.
    /// </remarks>
    public DateTimeOffset? RetentionClockStartedAt => Status switch
    {
        LeadStatus.Converted => ConvertedAt ?? UpdatedAt,
        LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Archived => UpdatedAt,
        _ => null,
    };

    /// <summary>
    /// Destroys the personal data this lead OWNS, irreversibly (BNF-B-19).
    /// </summary>
    /// <remarks>
    /// <b>What survives, and why.</b> The source, the campaign, the score, the status, the pipeline
    /// stage, the agency, the assignment and every date: that is the funnel history an institution
    /// measures itself on, and destroying it would make a retention rule a reporting outage. The
    /// id survives too, so an audit row that names this lead still resolves.
    /// </remarks>
    /// <returns>
    /// False when there was nothing to do — already anonymised. The caller uses it to count work
    /// rather than to detect an error.
    /// </returns>
    public bool Anonymize(DateTimeOffset now)
    {
        if (IsAnonymized)
            return false;

        // The name is replaced rather than blanked: FullName is NOT NULL, and several read sides
        // project it directly. A marker keeps those screens readable and says what happened.
        FullName  = "[anonymisé]";
        FirstName = null;
        LastName  = null;

        // Every direct identifier, including the blind indexes — leaving one would let somebody
        // confirm a guessed phone number or e-mail against a row whose data is supposedly gone,
        // which is the whole point of a blind index working.
        PhoneNumber     = "[anonymisé]";
        PhoneBlindIndex = null;
        Email           = null;
        EmailBlindIndex = null;
        LegacyEmailClear = null;
        NationalId      = null;
        CustomerReference = null;
        DateOfBirth     = null;
        Gender          = LeadGender.Unknown;

        PrincipalUsageName   = null;
        PrincipalAddressLine = null;
        PrincipalPostalCode  = null;
        PrincipalCity        = null;
        PrincipalCountryCode = null;

        CompanyName  = null;
        CompanyEmail = null;
        CompanyPhone = null;
        Website      = null;

        // Free text an agent typed about a person. A comment field is where an identity ends up
        // when no column fits it, so it goes whether or not it looks like it holds one.
        Comment    = null;
        LossReason = null;

        // The exact capture location is a personal datum of its own: a GPS point taken at a
        // walk-in is the prospect's doorstep.
        Location = null;

        // Web attribution that can single somebody out. The CAMPAIGN and the UTM source stay —
        // those are about the channel, not the person.
        LandingPage = null;
        Referrer    = null;
        ExternalId  = null;
        SocialPublicationId = null;
        SocialInteractionId = null;

        // The PEP classification is personal data AND a compliance record. It goes: the file it
        // justified has been closed for two years, and keeping a political exposure on a
        // destroyed lead is holding the most sensitive field of all for no remaining purpose.
        PrincipalPep   = PepStatus.NotExposed;
        PepDeclaredAt  = null;
        HasPepExposure = false;

        // The household members own their own identities — the caller anonymises them, because
        // they are separate rows and the batching has to see them.
        IsAnonymized = true;
        AnonymizedAt = now;
        UpdatedAt    = now;

        return true;
    }

    // ── Enrichment (BNF-B-02, BNF-B-04) ─────────────────────────────────────

    /// <summary>
    /// Completes the principal's identity after dispatching (BNF-B-02) — the structured names, the
    /// usage name, the birth date, the gender and the postal address.
    ///
    /// <para>
    /// <b>Null means "leave alone", not "clear".</b> Enrichment is incremental by nature: an agent
    /// adds the address today and the birth date next week, from a screen that posts only what it
    /// collected. A null that cleared would make the second save erase the first. Blank is how a
    /// caller clears a field, which is why the two are distinguished here and nowhere else in this
    /// aggregate.
    /// </para>
    ///
    /// <para>
    /// It does NOT touch the phone, the e-mail, the national id or the product line. Those have
    /// their own writers — <see cref="SetEmail"/>, <see cref="SetProductLine"/> — because each
    /// carries a side effect (a blind index, a policy change) that a bulk identity setter would
    /// silently skip.
    /// </para>
    /// </summary>
    /// <param name="countryCode">
    /// ISO 3166-1 alpha-2. Normalised and shape-checked here; whether those two letters name a
    /// real country is the validator's question, as on <c>PersonIdentity</c>.
    /// </param>
    /// <returns><c>LEAD_PRINCIPAL_COUNTRY_INVALID</c>, or the closed-lead refusal.</returns>
    public Result EnrichPrincipalIdentity(
        string? firstName,
        string? lastName,
        string? usageName,
        DateOnly? birthDate,
        LeadGender? gender,
        string? addressLine,
        string? postalCode,
        string? city,
        string? countryCode,
        DateTimeOffset now)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");

        string? country = null;
        if (countryCode is not null)
        {
            country = IsoCountry.Normalize(countryCode);
            if (country is not null && !IsoCountry.HasValidShape(country))
                return Result.Fail("LEAD_PRINCIPAL_COUNTRY_INVALID");
        }

        if (firstName   is not null) FirstName            = Blank(firstName);
        if (lastName    is not null) LastName             = Blank(lastName);
        if (usageName   is not null) PrincipalUsageName   = Blank(usageName);
        if (birthDate   is not null) DateOfBirth          = birthDate;
        if (gender      is not null) Gender               = gender.Value;
        if (addressLine is not null) PrincipalAddressLine = Blank(addressLine);
        if (postalCode  is not null) PrincipalPostalCode  = Blank(postalCode);
        if (city        is not null) PrincipalCity        = Blank(city);
        if (countryCode is not null) PrincipalCountryCode = country;

        // Structured names win over the captured FullName, the same rule as Update() — see there.
        if (FirstName is not null && LastName is not null)
            FullName = $"{FirstName} {LastName}";

        UpdatedAt = now;
        return Result.Ok();
    }

    // ── PEP (BNF-B-05) ───────────────────────────────────────────────────────

    /// <summary>
    /// Sets the PRINCIPAL's PEP status and, when it becomes a <see cref="PepLink.Self"/> exposure,
    /// propagates <see cref="PepLink.FamilyMember"/> onto the active household that has not been
    /// classified on its own (BNF-B-05).
    /// </summary>
    /// <param name="postMandateMonths">
    /// The tenant's <c>PepPostMandateMonths</c>. Needed because the exposure flag depends on it —
    /// see <see cref="RecalculateHasPepExposure"/>.
    /// </param>
    public Result SetPrincipalPepStatus(PepStatus pep, DateTimeOffset now, int postMandateMonths)
    {
        ArgumentNullException.ThrowIfNull(pep);

        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");

        PrincipalPep  = pep;
        // The declaration is the EVENT, not the value: stamping it here is what lets the
        // completeness check tell "answered no" from "never asked" (see PepDeclaredAt).
        PepDeclaredAt = now;

        // Only onto members nobody has decided about. A member carrying an OverrideReason has been
        // classified by an agent, and overwriting that would discard a judgement BNF-B-05
        // explicitly allows them to make.
        if (pep is { IsPep: true, Link: PepLink.Self })
        {
            foreach (var member in ActiveHouseholdMembers.Where(m => m.Pep.OverrideReason is null))
                member.SetPepStatus(pep.AsFamilyMemberLink(), now);
        }

        UpdatedAt = now;
        return RecalculateHasPepExposure(now, postMandateMonths);
    }

    /// <summary>Sets one household member's PEP status.</summary>
    public Result SetHouseholdMemberPepStatus(
        Guid memberId, PepStatus pep, DateTimeOffset now, int postMandateMonths)
    {
        ArgumentNullException.ThrowIfNull(pep);

        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");

        var member = _householdMembers.FirstOrDefault(m => m.Id == memberId);
        if (member is null)
            return Result.Fail("HOUSEHOLD_MEMBER_NOT_FOUND");

        var set = member.SetPepStatus(pep, now);
        if (set.IsFailure)
            return set;

        UpdatedAt = now;
        return RecalculateHasPepExposure(now, postMandateMonths);
    }

    /// <summary>
    /// Recomputes <see cref="HasPepExposure"/> over the principal and the ACTIVE household, and
    /// raises <c>LeadPepExposureChanged</c> when the answer moved.
    ///
    /// <para>
    /// The ONE writer of that flag, and the reason it is a method rather than a computed property:
    /// the event has to fire exactly when the value changes, and a property cannot notice that.
    /// Called by every PEP mutator and by the nightly recalculation that catches a post-mandate
    /// window expiring — exposure can lapse with nobody touching the lead.
    /// </para>
    /// </summary>
    public Result RecalculateHasPepExposure(DateTimeOffset now, int postMandateMonths)
    {
        var asOf = DateOnly.FromDateTime(now.UtcDateTime);

        var exposed = PrincipalPep.IsExposedAt(asOf, postMandateMonths)
                      || _householdMembers.Any(m => m.IsPepExposedAt(asOf, postMandateMonths));

        if (exposed == HasPepExposure)
            return Result.Ok();

        HasPepExposure = exposed;
        UpdatedAt      = now;

        RaiseDomainEvent(new LeadPepExposureChangedDomainEvent(Id, exposed));
        return Result.Ok();
    }

    // ── Product line (BNF-B-01) ──────────────────────────────────────────────

    /// <summary>
    /// Resolves, or clears, the M12 product line this request is about. Server-set — see
    /// <see cref="ProductLineId"/> for why no capture contract carries it.
    /// </summary>
    public Result SetProductLine(Guid? productLineId)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");

        // Guid.Empty is what a malformed JSON guid deserialises to in this API
        // (see GuidConverter in Program.cs), so it must not be stored as a real reference.
        ProductLineId = productLineId == Guid.Empty ? null : productLineId;
        UpdatedAt     = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    // ── Existing-customer link (BNF-B-22) ────────────────────────────────────

    /// <summary>
    /// Attaches this lead to an existing M01 client.
    /// </summary>
    /// <param name="isAutomatic">
    /// True only for a UNIQUE, EXACT match found by the capture-time matcher. A partial or multiple
    /// match is never attached here — it is surfaced as a suggestion for an agent to confirm, and
    /// this flag is what keeps the two apart afterwards (BNF-B-22).
    /// </param>
    /// <param name="reason">
    /// Required for a manual attachment. An attachment decides which client record the entire
    /// request then reads its identity, household, PEP and FATCA from (BNF-B-23), so one made
    /// without a stated reason is one nobody can review. An automatic one needs none: the matcher's
    /// rule IS the reason.
    /// </param>
    public Result AttachToCustomer(Guid customerId, bool isAutomatic, string? reason)
    {
        if (customerId == Guid.Empty)
            return Result.Fail("CUSTOMER_ID_REQUIRED");
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");
        if (!isAutomatic && string.IsNullOrWhiteSpace(reason))
            return Result.Fail("CUSTOMER_LINK_REASON_REQUIRED");

        // Idempotent on the same client: a replayed matcher or a double-clicked button must not
        // overwrite a manual decision with an automatic one, nor re-raise the event.
        if (CustomerId == customerId)
            return Result.Ok();

        CustomerId              = customerId;
        CustomerLinkIsAutomatic = isAutomatic;
        CustomerLinkReason      = Blank(reason);
        CustomerLinkedAt        = DateTimeOffset.UtcNow;
        UpdatedAt               = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadLinkedToCustomerDomainEvent(Id, customerId, isAutomatic));
        return Result.Ok();
    }

    /// <summary>
    /// Detaches the lead from the client it was attached to — the correction path for a wrong
    /// match. Always manual, so a reason is always required.
    /// </summary>
    public Result DetachFromCustomer(string reason)
    {
        if (Status is LeadStatus.Converted or LeadStatus.Archived)
            return Result.Fail("Cannot update a closed lead.");
        if (CustomerId is not { } previous)
            return Result.Fail("LEAD_NOT_LINKED_TO_CUSTOMER");
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail("CUSTOMER_LINK_REASON_REQUIRED");

        CustomerId              = null;
        CustomerLinkIsAutomatic = false;
        // The reason for DETACHING is kept, not cleared: it is the explanation that matters most
        // afterwards — somebody decided this request was not that client's.
        CustomerLinkReason      = reason.Trim();
        CustomerLinkedAt        = null;
        UpdatedAt               = DateTimeOffset.UtcNow;

        RaiseDomainEvent(new LeadDetachedFromCustomerDomainEvent(Id, previous));
        return Result.Ok();
    }

    /// <summary>
    /// Applies field values from the source lead onto this (target) lead during a merge.
    /// Only fields with a non-null source value AND whose corresponding preference flag
    /// is true are overwritten. Call before <see cref="MergeInto"/> on the source.
    /// </summary>
    internal void ApplyMergeOverrides(
        string? email             = null,
        string? emailBlindIndex   = null,
        string? firstName         = null,
        string? lastName          = null,
        string? nationalId        = null,
        string? customerReference = null,
        DateOnly? dateOfBirth     = null,
        LeadGender? gender        = null,
        Money? desiredAmount      = null,
        string? comment           = null,
        string? companyName       = null,
        string? companyEmail      = null,
        string? companyPhone      = null,
        string? website           = null,
        GeoPoint? location        = null)
    {
        // The pair moves together, as everywhere else. A merge that took the source's address
        // and left the target's old index behind would make the merged lead match the wrong
        // address in every later dedup — and nothing would say so.
        if (email is not null)
        {
            Email           = email;
            EmailBlindIndex = emailBlindIndex;
        }
        if (firstName         is not null) FirstName         = firstName;
        if (lastName          is not null) LastName          = lastName;
        if (nationalId        is not null) NationalId        = nationalId;
        if (customerReference is not null) CustomerReference = customerReference;
        if (dateOfBirth.HasValue)          DateOfBirth       = dateOfBirth;
        if (gender.HasValue)               Gender            = gender.Value;
        if (desiredAmount     is not null) DesiredAmount     = desiredAmount;
        if (comment           is not null) Comment           = comment;
        if (companyName       is not null) CompanyName       = companyName;
        if (companyEmail      is not null) CompanyEmail      = companyEmail;
        if (companyPhone      is not null) CompanyPhone      = companyPhone;
        if (website           is not null) Website           = website;
        if (location          is not null) Location          = location;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Archives this lead as the source of a merge, raising a structured domain event
    /// so that the merge appears in the audit trail and timeline.
    /// </summary>
    public Result MergeInto(Guid targetLeadId, Guid mergedBy)
    {
        var closeResult = Close(
            LeadCloseReason.Archived,
            $"Merged into lead {targetLeadId} by {mergedBy}");

        if (closeResult.IsFailure)
            return closeResult;

        RaiseDomainEvent(new Events.LeadMergedDomainEvent(Id, targetLeadId, mergedBy));
        return Result.Ok();
    }

    /// <summary>
    /// Raises <see cref="Events.DuplicateDismissedDomainEvent"/> on this lead so the
    /// timeline interceptor can dispatch it after the SaveChanges commit.
    /// Called by <c>DismissDuplicateHandler</c> since <see cref="DuplicateDismissal"/>
    /// is not an aggregate root and cannot raise domain events itself.
    /// </summary>
    internal void RaiseDismissalEvent(Guid candidateLeadId, Guid dismissedBy)
        => RaiseDomainEvent(new Events.DuplicateDismissedDomainEvent(Id, candidateLeadId, dismissedBy));

    /// <summary>Raises <see cref="Events.ConsentRecordedDomainEvent"/>. Called by <c>RecordConsentHandler</c>.</summary>
    internal void RaiseConsentRecordedEvent(Guid consentId, string consentType)
        => RaiseDomainEvent(new Events.ConsentRecordedDomainEvent(Id, consentId, consentType));

    /// <summary>Raises <see cref="Events.ConsentWithdrawnDomainEvent"/>. Called by <c>WithdrawConsentHandler</c>.</summary>
    internal void RaiseConsentWithdrawnEvent(Guid consentId, string consentType)
        => RaiseDomainEvent(new Events.ConsentWithdrawnDomainEvent(Id, consentId, consentType));

    /// <summary>Raises <see cref="Events.SlaEscalatedDomainEvent"/>. Called by <c>CheckSlaBreachesJob</c>.</summary>
    internal void RaiseSlaEscalatedEvent(Guid assignmentId, Guid agentId, int escalationDepth)
        => RaiseDomainEvent(new Events.SlaEscalatedDomainEvent(Id, assignmentId, agentId, escalationDepth));

    /// <summary>
    /// Transitions a New lead to Open — signals the first manual or system
    /// action has been taken on this lead.
    /// </summary>
    public Result Reopen()
    {
        if (Status != LeadStatus.New)
            return Result.Fail("Only a New lead can be opened.");

        var previous = Status;
        Status    = LeadStatus.Open;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Moves an active lead into the Nurturing state — used when a lead is
    /// not yet ready to be qualified but should be kept warm with regular
    /// touchpoints.
    /// </summary>
    public Result Nurture()
    {
        if (Status is LeadStatus.Converted or LeadStatus.Lost
                   or LeadStatus.Disqualified or LeadStatus.Archived)
            return Result.Fail("LEAD_CANNOT_BE_NURTURED_FROM_CURRENT_STATUS");

        if (Status == LeadStatus.Nurturing)
            return Result.Fail("Lead is already in Nurturing.");

        var previous = Status;
        Status    = LeadStatus.Nurturing;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Recycles a closed or nurturing lead back into the active pipeline.
    /// Resets the score to 0 so the lead must be re-qualified.
    /// </summary>
    public Result Recycle(LeadSource? newSource = null, string? newCampaign = null)
    {
        if (Status is not (LeadStatus.Lost or LeadStatus.Disqualified or LeadStatus.Nurturing))
            return Result.Fail("Only Lost, Disqualified or Nurturing leads can be recycled.");

        var previous = Status;
        Status = LeadStatus.Recycled;

        if (newSource.HasValue) Source = newSource.Value;
        if (newCampaign is not null) Campaign = newCampaign.Trim();

        // Reset score: the lead must go through qualification again.
        Score     = 0;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }

    /// <summary>
    /// Reactivates a Recycled lead back into the Open state so it re-enters
    /// the qualification/scoring pipeline (US-M13-161).
    /// </summary>
    public Result Reactivate()
    {
        if (Status != LeadStatus.Recycled)
            return Result.Fail("ONLY_RECYCLED_LEADS_CAN_BE_REACTIVATED");

        var previous = Status;
        Status    = LeadStatus.Open;
        UpdatedAt = DateTimeOffset.UtcNow;
        RaiseDomainEvent(new LeadStatusChangedDomainEvent(Id, previous, Status));
        return Result.Ok();
    }
}
