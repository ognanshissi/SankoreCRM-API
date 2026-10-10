namespace Sankore.Modules.Leads.Features.Enrichment;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Fatca;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// How complete a lead's file is, and whether it may convert (BNF-B-04).
///
/// <para>
/// <b>ONE calculator, and that is the point of the story.</b> The percentage on the lead screen,
/// the per-person list of what is missing, and the gate inside <c>ConvertLeadHandler</c> are the
/// same computation. Before this existed, the conversion gate checked questionnaires only, and a
/// screen computing its own percentage would have shown 100% on a lead the conversion then
/// refused — the single worst outcome for a progress indicator.
/// </para>
///
/// <para>
/// <b>Everything it requires is configured, nothing is hardcoded.</b> Which fields are required
/// comes from the product line's <c>RequiredFieldsBeforeConversion</c>; whether FATCA applies and
/// how long a PEP stays exposed come from the tenant's compliance settings; which questionnaires
/// are owed comes from the templates attached to the line. A platform-wide list of required fields
/// would be the same mistake as a platform-wide beneficiary policy.
/// </para>
///
/// <para>
/// <b>An outage makes a lead INCOMPLETE, never complete.</b> Each cross-module read fails into a
/// <c>COMPLETENESS_CHECK_UNAVAILABLE</c> gap that blocks the conversion. Answering "complete"
/// because M12 was unreachable is the one failure mode a gate must not have, and it is the reason
/// this class never catches an exception without adding a gap.
/// </para>
/// </summary>
internal sealed class LeadCompletenessCalculator(
    LeadsDbContext db,
    IAdministrationModule administration,
    ICustomersModule customers,
    IKycModule kyc,
    QuestionnaireCompletenessChecker questionnaires,
    FatcaSubjectPlanner fatcaPlanner,
    ILeadSettings settings)
{
    /// <summary>What the conversion reports when the file is not complete.</summary>
    internal const string Incomplete = "LEAD_NOT_COMPLETE";

    /// <summary>
    /// Computes the report. Never fails — an unreachable module is a gap, not an exception, so a
    /// screen always has something to render and the conversion always has an answer.
    /// </summary>
    /// <remarks>
    /// <paramref name="lead"/> must have been loaded with <c>Include(l =&gt; l.HouseholdMembers)</c>.
    /// A lead loaded without them has an EMPTY household here and would be reported complete while
    /// owing a questionnaire per child — the same trap documented on <c>HouseholdWriteContext</c>.
    /// </remarks>
    internal async Task<LeadCompletenessDto> ComputeAsync(Lead lead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lead);

        var gaps = new List<CompletenessGap>();
        var applicable = 0;
        var satisfied = 0;

        void Check(bool ok, Func<CompletenessGap> gap)
        {
            applicable++;
            if (ok) satisfied++;
            else gaps.Add(gap());
        }

        // ── 1. The product line, first (§3.3) ────────────────────────────────
        //
        // Reported FIRST because nothing else can be required without it: the policy that says
        // what to collect is reached THROUGH the line. Without it the most permissive path — a
        // lead whose product nobody identified — would also be the easiest to convert.
        //
        // Whether it BLOCKS is the tenant's switch, and it ships off. The reasoning is on
        // LeadSettingKeys.RequireProductLineToConvert: every lead already in the database has no
        // product line, so a refusal on by default would make none of them convertible. Reported
        // either way, so the gap is visible before anybody turns the switch on.
        var productLineBlocks = await RequireProductLineAsync(lead.TenantId, ct);

        Check(lead.ProductLineId is not null,
              () => new CompletenessGap(
                  Code:         CompletenessCodes.ProductLineNotResolved,
                  SubjectLabel: null,
                  Detail:       null,
                  ActionHint:   null,
                  Blocking:     productLineBlocks));

        var policy = await TryReadPolicyAsync(lead, ct);
        if (policy.Unavailable)
        {
            Check(false, () => new CompletenessGap(
                CompletenessCodes.CheckUnavailable, null, "PRODUCT_POLICY"));
        }

        var requiredFields = policy.Value?.RequiredFieldsBeforeConversion ?? [];

        // ── 2. Required fields, per person ───────────────────────────────────
        var subjects = BuildSubjects(lead);

        foreach (var subject in subjects)
        {
            foreach (var code in requiredFields)
            {
                // A principal-only field is not asked of a spouse: there is no column for a
                // spouse's telephone number, so requiring it would open a gap nobody can close.
                if (!subject.IsPrincipal
                    && PersonFieldCode.ScopeOf(code) == PersonFieldScope.PrincipalOnly)
                    continue;

                var ok = RequiredFieldEvaluator.IsSatisfied(code, subject, lead);

                Check(ok, () => new CompletenessGap(
                    Code:         code == PersonFieldCode.PepStatus
                                      ? CompletenessCodes.PepNotDeclared
                                      : CompletenessCodes.MissingRequiredField,
                    SubjectLabel: subject.Label,
                    Detail:       code,
                    ActionHint:   RequiredFieldEvaluator.ActionHintFor(code, subject)));
            }
        }

        // ── 3. Questionnaires (BNF-B-08) ─────────────────────────────────────
        //
        // Reuses the checker the conversion gate already used, rather than recomputing the plan:
        // two implementations of "which questionnaires are owed" would eventually disagree, and
        // the one in the gate is the one that matters.
        var missingQuestionnaires = await questionnaires.MissingAsync(lead, ct);

        foreach (var missing in missingQuestionnaires)
        {
            applicable++;
            gaps.Add(new CompletenessGap(
                Code:         CompletenessCodes.MissingQuestionnaire,
                SubjectLabel: missing.SubjectLabel,
                Detail:       missing.TemplateName));
        }

        // Answered questionnaires count towards the percentage too, or a lead that has completed
        // six of seven would read the same as one that has completed none. One check each —
        // applicable AND satisfied — which is why the same number is added twice.
        var answered = await db.QualificationResponses
            .CountAsync(r => r.LeadId == lead.Id
                          && r.Status == QualificationResponseStatus.Completed, ct);
        applicable += answered;
        satisfied  += answered;

        // ── 4. FATCA (BNF-B-11) ──────────────────────────────────────────────
        await AddFatcaChecksAsync(lead, Check, ct);

        // ── 5. An existing customer's file must be up to date (BNF-B-23) ─────
        if (lead.Kind == LeadKind.ExistingCustomer)
            await AddCustomerChecksAsync(lead, policy.Value, Check, ct);

        // A lead with nothing applicable is 100% complete, not 0% — "nothing is required" is a
        // satisfied state, and 0/0 would show every current-account lead as empty forever.
        var percentage = applicable == 0 ? 100 : (int)Math.Round(satisfied * 100.0 / applicable);

        return new LeadCompletenessDto(
            LeadId:     lead.Id,
            Kind:       lead.Kind.ToString(),
            Percentage: Math.Clamp(percentage, 0, 100),
            Satisfied:  satisfied,
            Applicable: applicable,
            CanConvert: !gaps.Any(g => g.Blocking),
            Gaps:       gaps,
            MissingQuestionnaires: missingQuestionnaires);
    }

    /// <summary>
    /// Whether an unresolved product line refuses the conversion for this tenant.
    /// </summary>
    /// <remarks>
    /// An unreadable setting falls back to <c>false</c> — NOT the usual fail-closed posture, and
    /// deliberately so. Failing closed here would refuse every conversion in the tenant because a
    /// settings row could not be read, which is a worse outcome than letting through a lead whose
    /// product line nobody resolved. The restrictive default belongs where a compliance rule is at
    /// stake (the PEP window, the FATCA toggle); this switch is a rollout control.
    /// </remarks>
    private async Task<bool> RequireProductLineAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            return await settings.GetBoolAsync(
                tenantId, LeadSettingKeys.RequireProductLineToConvert, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The people the check is about: the principal, then every ACTIVE household member.
    /// </summary>
    /// <remarks>
    /// <b>An existing customer's principal is skipped</b> (BNF-B-23: "sans ressaisie"). Their
    /// identity lives in M01 and the leads schema deliberately holds no copy of it, so requiring
    /// those fields here would ask an agent to re-type what the client record already has — and a
    /// copy is exactly what the story forbids. What replaces them is
    /// <see cref="AddCustomerChecksAsync"/>: the client file itself must be up to date.
    ///
    /// <para>
    /// The HOUSEHOLD is still read from the lead even for an attached lead, and that is a
    /// transitional position, not the target. BNF-B-23 puts the household on the client record;
    /// M01 does not expose one yet (it arrives with <c>ClientRelationship</c> in the next lot), and
    /// ignoring the members a lead already carries would silently drop them from the check.
    /// </para>
    /// </remarks>
    private static List<CompletenessSubject> BuildSubjects(Lead lead)
    {
        var subjects = new List<CompletenessSubject>();
        var principalIdentity = lead.PrincipalIdentity;

        if (lead.Kind == LeadKind.NewProspect)
        {
            subjects.Add(new CompletenessSubject(
                Label:                    PersonLabel.Principal,
                Identity:                 principalIdentity,
                MemberId:                 null,
                IsPrincipal:              true,
                PepDeclared:              lead.PepDeclaredAt is not null,
                InheritsPrincipalAddress: false));
        }

        subjects.AddRange(lead.ActiveHouseholdMembers.Select(m => new CompletenessSubject(
            Label: PersonLabel.For(m),
            // Resolved, so an inherited address is evaluated as the address the member actually
            // has. Reading m.Identity directly reports a child as having no address at all.
            Identity:                 m.ResolveIdentity(principalIdentity),
            MemberId:                 m.Id,
            IsPrincipal:              false,
            PepDeclared:              m.PepDeclaredAt is not null,
            InheritsPrincipalAddress: m.UsesPrincipalAddress)));

        return subjects;
    }

    /// <summary>
    /// One check per FATCA subject: an active declaration exists, and its documentation is in.
    /// Nothing at all when the tenant has FATCA off — BNF-B-11 is explicit that it is then neither
    /// required, exposed nor counted, so it must not move the percentage either.
    /// </summary>
    private async Task AddFatcaChecksAsync(
        Lead lead, Action<bool, Func<CompletenessGap>> check, CancellationToken ct)
    {
        var scope = await fatcaPlanner.ResolveAsync(lead, ct);

        if (scope.IsFailure)
        {
            check(false, () => new CompletenessGap(
                CompletenessCodes.CheckUnavailable, null, "FATCA_SETTINGS"));
            return;
        }

        if (!scope.Value.Enabled || scope.Value.Subjects.Count == 0)
            return;

        var active = await db.FatcaDeclarations
            .Where(d => d.LeadId == lead.Id && d.SupersededAt == null)
            .Select(d => new { d.SubjectType, d.SubjectId, d.Status })
            .ToListAsync(ct);

        foreach (var (type, id) in scope.Value.Subjects)
        {
            var declaration = active.FirstOrDefault(d => d.SubjectType == type && d.SubjectId == id);
            var label = PersonLabel.For(lead, type, id);

            check(declaration is not null, () => new CompletenessGap(
                CompletenessCodes.FatcaDeclarationMissing, label, null));

            if (declaration is null)
                continue;

            // Counted and reported but NOT blocking — see CompletenessGap.Blocking.
            check(declaration.Status != FatcaStatus.IndiciaPending,
                  () => new CompletenessGap(
                      Code:         CompletenessCodes.FatcaDocumentationOutstanding,
                      SubjectLabel: label,
                      Detail:       null,
                      ActionHint:   null,
                      Blocking:     false));
        }
    }

    /// <summary>
    /// The client record behind an attached lead has to be usable (BNF-B-23: "vérifie que la fiche
    /// client est à jour"). Three questions, each with its own code so the agent is told which:
    /// does the client exist and is it active, is its KYC approved and current, and does it reach
    /// the tier this product line requires.
    ///
    /// <para>
    /// Every gap here carries <c>ActionHint = "FIX_ON_CUSTOMER"</c>, because none of them can be
    /// fixed on the lead — the story asks for exactly that: "un manque bloquant pointe vers
    /// l'action à mener sur la fiche client".
    /// </para>
    /// </summary>
    private async Task AddCustomerChecksAsync(
        Lead lead,
        BeneficiaryPolicyDto? policy,
        Action<bool, Func<CompletenessGap>> check,
        CancellationToken ct)
    {
        const string FixOnCustomer = "FIX_ON_CUSTOMER";
        var customerId = lead.CustomerId!.Value;

        ClientSummary? client;
        KycStatus? kycStatus;
        KycLimits? limits;

        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            client = await customers.GetClientSummaryAsync(lead.TenantId, customerId, ct);
            kycStatus = await kyc.GetStatusAsync(lead.TenantId, customerId, ct);

            // Only asked when the line requires a tier — GetLimitsAsync is a cached read but it is
            // still a read, and a line with no tier requirement has no use for the answer.
            limits = policy?.RequiredKycTier is null
                ? null
                : await kyc.GetLimitsAsync(lead.TenantId, customerId, ct);

            suppressed.Complete();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            check(false, () => new CompletenessGap(
                CompletenessCodes.CheckUnavailable, PersonLabel.Principal, "CUSTOMER_FILE"));
            return;
        }

        if (client is null)
        {
            // The lead names a client M01 does not have. Not a validation failure of the lead — the
            // attachment was valid when it was made and the client may since have been merged away
            // — but it must block, because the whole point of an attached lead is that the client
            // record is where its identity comes from.
            check(false, () => new CompletenessGap(
                CompletenessCodes.CustomerNotFound, PersonLabel.Principal, null, FixOnCustomer));
            return;
        }

        check(IsSellableStatus(client.Status),
              () => new CompletenessGap(
                  CompletenessCodes.CustomerNotActive, PersonLabel.Principal,
                  client.Status, FixOnCustomer));

        check(kycStatus == KycStatus.Approved, () => new CompletenessGap(
            Code: kycStatus switch
            {
                KycStatus.Expired  => CompletenessCodes.CustomerKycExpired,
                KycStatus.Rejected => CompletenessCodes.CustomerKycRejected,
                _                  => CompletenessCodes.CustomerKycNotApproved,
            },
            SubjectLabel: PersonLabel.Principal,
            Detail:       kycStatus?.ToString() ?? "NoFile",
            ActionHint:   FixOnCustomer));

        if (policy?.RequiredKycTier is not { } requiredTier)
            return;

        // A null from GetLimitsAsync means no file, a rejected one or a suspended one — never "no
        // limits". The KYC check above has already reported that, so this one simply fails rather
        // than re-reporting it under a second code.
        check(limits is not null
              && string.Equals(limits.Tier, requiredTier, StringComparison.OrdinalIgnoreCase),
              () => new CompletenessGap(
                  Code:         CompletenessCodes.CustomerKycTierInsufficient,
                  SubjectLabel: PersonLabel.Principal,
                  Detail:       requiredTier,
                  ActionHint:   FixOnCustomer));
    }

    /// <summary>
    /// Which client statuses a new subscription may be attached to. Compared by NAME because the
    /// contract exposes a string — M13 may not reference M01's enum — and the list is the
    /// permissive one: anything live. Archived, suspended and merged are not places to sell.
    /// </summary>
    private static bool IsSellableStatus(string status)
        => status is not ("Archived" or "Suspended" or "Merged" or "Blocked");

    /// <summary>
    /// The line's policy, with the outage distinguished from a line that genuinely has none.
    /// </summary>
    /// <remarks>
    /// Read here rather than through <c>HouseholdPolicyResolver</c>: that service answers a
    /// different question (may this household be edited, and within what limits) and drops the
    /// required-fields list and the KYC tier on the way out. The read is cached per line by
    /// <c>ProductCatalogCache</c>, so asking twice in one request costs nothing.
    /// </remarks>
    private async Task<(BeneficiaryPolicyDto? Value, bool Unavailable)> TryReadPolicyAsync(
        Lead lead, CancellationToken ct)
    {
        if (lead.ProductLineId is not { } lineId)
            return (null, false);

        try
        {
            using var suppressed = new System.Transactions.TransactionScope(
                System.Transactions.TransactionScopeOption.Suppress,
                System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

            var policy = await administration.GetBeneficiaryPolicyAsync(lead.TenantId, lineId, ct);
            suppressed.Complete();

            // A DANGLING line id — the product was retired, or the id was never valid. Treated as
            // "no policy" and not as an outage, the same call HouseholdPolicyResolver makes: the
            // reference is opaque by construction, and a retired product must degrade rather than
            // strand every lead that pointed at it.
            return (policy, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, true);
        }
    }
}
