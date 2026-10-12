namespace Sankore.Modules.Integration.Features.Commands;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Builds the key that makes a write safe to replay (INT-05, criterion 2).
///
/// <para>
/// Deterministic, and derived from what the write IS: the tenant, the connection that will
/// execute it, the operation, the CRM entity it is about, and the operation's own discriminating
/// arguments. Never a clock and never a <see cref="Guid.NewGuid"/> — either of those would make
/// the same request produce a second key, the unique index would let a second row through, and
/// the customer would be created twice in the core banking system. That is the one failure this
/// whole mechanism exists to prevent, so the rule is enforced here, in one place, rather than
/// left to each caller's discretion.
/// </para>
///
/// <para>
/// The connection is part of the key on purpose. The same customer legitimately owes a creation
/// to each external system a tenant is connected to, and a tenant that replaces its CBS owes the
/// creation again on the new connection.
/// </para>
///
/// <para>
/// Shape: <c>&lt;CommandType&gt;:&lt;64 lower-case hex&gt;</c>. The hash bounds the length — a
/// product code or a beneficiary list has no upper bound and the column holds 200 characters —
/// while the prefix keeps a row readable to whoever is looking at the rejection queue. Longest
/// possible value today is <c>SubmitLoanApplication:</c> plus 64 hex = 86 characters, so the
/// bound holds with room to spare for a command type nobody has invented yet.
/// </para>
/// </summary>
internal static class IdempotencyKeyFactory
{
    /// <summary>The <c>idempotency_key</c> column's width. Asserted by a test, not assumed.</summary>
    internal const int MaxLength = 200;

    /// <summary>
    /// The general form. <paramref name="discriminators"/> are the arguments that make two
    /// otherwise identical requests DIFFERENT writes — a product code, a KYC level, an effective
    /// date. Order matters and is part of the key: they are joined positionally, so a caller must
    /// always pass them in the same order for the same command type (which is why every call site
    /// below is a named helper rather than this method).
    /// </summary>
    internal static IdempotencyKey For(
        Guid tenantId,
        Guid connectionId,
        CommandType commandType,
        string entityType,
        Guid crmId,
        params object?[] discriminators)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        var canonical = new StringBuilder()
            // "N" rather than "D": no separators to be confused with the field separator below.
            .Append(tenantId.ToString("N"))
            .Append('|').Append(connectionId.ToString("N"))
            .Append('|').Append(commandType)
            // Upper-cased: the entity type is a repo-convention string and "customer" typed by
            // one caller must not owe a second write next to "Customer" typed by another.
            .Append('|').Append(entityType.Trim().ToUpperInvariant())
            .Append('|').Append(crmId.ToString("N"));

        foreach (var discriminator in discriminators)
            canonical.Append('|').Append(Canonicalise(discriminator));

        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

        return new IdempotencyKey($"{commandType}:{hash}");
    }

    /// <summary>One creation per customer, per connection. No further discriminator exists.</summary>
    internal static IdempotencyKey ForCustomerCreation(
        Guid tenantId, Guid connectionId, Guid crmCustomerId)
        => For(tenantId, connectionId, CommandType.CreateCustomer,
            IntegrationEntityTypes.Customer, crmCustomerId);

    /// <summary>
    /// The LEVEL discriminates. A customer promoted to Simplified and later to Full owes two
    /// distinct writes; without the level in the key the second would be swallowed as a replay of
    /// the first and the CBS would never learn about the promotion.
    /// </summary>
    internal static IdempotencyKey ForKycLevelUpdate(
        Guid tenantId, Guid connectionId, Guid crmCustomerId, KycLevel level)
        => For(tenantId, connectionId, CommandType.SetKycLevel,
            IntegrationEntityTypes.Customer, crmCustomerId, level);

    /// <summary>
    /// The PRODUCT CODE discriminates: a customer opening a savings account and a current account
    /// owes two openings, and a double click on one product owes one.
    /// </summary>
    internal static IdempotencyKey ForAccountOpening(
        Guid tenantId, Guid connectionId, Guid crmCustomerId, string productCode)
        => For(tenantId, connectionId, CommandType.OpenAccount,
            IntegrationEntityTypes.Account, crmCustomerId, productCode);

    /// <summary>
    /// The product and the effective date discriminate (the pair ASS-04 subscribes on). The
    /// premium is deliberately NOT in the key: a corrected premium on the same product and the
    /// same effective date is the same subscription, and keying on it would subscribe the
    /// customer twice.
    /// </summary>
    internal static IdempotencyKey ForPolicySubscription(
        Guid tenantId, Guid connectionId, Guid crmCustomerId, Guid crmProductId, DateOnly effectiveDate)
        => For(tenantId, connectionId, CommandType.SubscribePolicy,
            IntegrationEntityTypes.Policy, crmCustomerId, crmProductId, effectiveDate);

    /// <summary>
    /// A subscription driven by an M01 <c>ProductHolding</c> (BNF-B-16). The HOLDING is the entity
    /// and the CLAUSE VERSION discriminates.
    ///
    /// <para>
    /// Not <see cref="ForPolicySubscription"/>: that one keys by customer and product because it
    /// predates any CRM-side contract record, so two subscriptions to the same product on the same
    /// effective date collapse into one — correct when the CRM holds nothing, wrong when it holds
    /// two distinct holdings. And the version is in the key because an avenant IS a second write:
    /// without it a changed clause would be swallowed as a replay and the insurer would never
    /// learn about it.
    /// </para>
    /// </summary>
    internal static IdempotencyKey ForHoldingSubscription(
        Guid tenantId, Guid connectionId, Guid crmHoldingId, int clauseVersion)
        => For(tenantId, connectionId, CommandType.SubscribePolicy,
            IntegrationEntityTypes.Policy, crmHoldingId, clauseVersion);

    /// <summary>
    /// The premium debit of one subscription attempt (ASS-05, first step).
    ///
    /// <para>
    /// Keyed on the SUBSCRIPTION and nothing else, which is what makes a retried subscription
    /// request attach to the debit already in flight instead of taking the customer's money
    /// twice. The amount is deliberately absent for the same reason it is absent from
    /// <see cref="ForPolicySubscription"/>: a corrected premium on the same subscription is the
    /// same debit, and keying on it would debit twice.
    /// </para>
    ///
    /// <para>
    /// The entity type is <c>Account</c> — a debit acts on an account and creates no addressable
    /// entity of its own, which is why <c>AcknowledgementApplier</c> maps it to no reference at
    /// all. The subscription is the CRM id because that is what makes this debit unique.
    /// </para>
    /// </summary>
    internal static IdempotencyKey ForPremiumDebit(
        Guid tenantId, Guid connectionId, Guid subscriptionId)
        => For(tenantId, connectionId, CommandType.DebitAccount,
            IntegrationEntityTypes.Account, subscriptionId);

    /// <summary>
    /// One attempt at collecting one premium instalment (ASS-08).
    ///
    /// <para>
    /// The ATTEMPT is in the key, unlike <see cref="ForPremiumDebit"/>'s. A subscription debits
    /// once and a retried request must attach to it; an INSTALMENT is retried deliberately, on
    /// the product's own schedule, and each attempt is a genuine second ask of the core banking
    /// system. Without the attempt in the key the second one would be swallowed as a replay and
    /// the instalment would sit in Failed for ever while the platform believed it had retried.
    /// </para>
    /// </summary>
    internal static IdempotencyKey ForInstalmentDebit(
        Guid tenantId, Guid connectionId, Guid instalmentId, int attempt)
        => For(tenantId, connectionId, CommandType.DebitAccount,
            IntegrationEntityTypes.Account, instalmentId, attempt);

    /// <summary>
    /// The reversal of a premium debit (ASS-05, compensating step).
    ///
    /// <para>
    /// <b>The ATTEMPT is in the key, and it has to be.</b> A reversal the core banking system
    /// refuses leaves the subscription in <c>PremiumRefundDue</c>, which this module does not
    /// retry automatically — an operator re-attempts it, and
    /// <c>InsuranceSubscription.ReversalAttempts</c> exists precisely so they know the CBS has
    /// already said no once. Without the attempt in the key that second attempt would be
    /// swallowed as a replay of the first, and the customer would stay un-refunded with the
    /// platform reporting that a refund had been requested.
    /// </para>
    ///
    /// <para>
    /// This is the same argument <see cref="ForHoldingSubscription"/> makes about a clause
    /// version, in the one place where getting it wrong costs a customer money rather than an
    /// insurer an amendment.
    /// </para>
    /// </summary>
    internal static IdempotencyKey ForDebitReversal(
        Guid tenantId, Guid connectionId, Guid subscriptionId, int attempt)
        => For(tenantId, connectionId, CommandType.ReverseDebit,
            IntegrationEntityTypes.Account, subscriptionId, attempt);

    /// <summary>
    /// The policy, the date of the loss and its nature discriminate. Two losses on the same
    /// policy on the same day are rare and real (a fire and a theft), so the nature is in;
    /// the free-text description is not, because retyping it must not declare a second claim.
    /// </summary>
    internal static IdempotencyKey ForClaimDeclaration(
        Guid tenantId, Guid connectionId, Guid crmCustomerId,
        string externalPolicyId, DateOnly occurredOn, string nature)
        => For(tenantId, connectionId, CommandType.DeclareClaim,
            IntegrationEntityTypes.Claim, crmCustomerId, externalPolicyId, occurredOn, nature);

    /// <summary>
    /// One claim piece, keyed by the DOCUMENT and not by the dossier.
    ///
    /// <para>
    /// The digest would be the obvious discriminator and is the wrong one: the same file may
    /// legitimately be re-uploaded after a refusal, producing a second row the insurer has to
    /// receive, and <c>ux_ins_claim_document_digest</c> already refuses a true duplicate on the
    /// same dossier. Keying by the row means "send this piece once" — which is exactly what a
    /// replay must not turn into two attachments on the insurer's side.
    /// </para>
    ///
    /// <para>
    /// No attempt in the key, for the same reason a premium debit has none: a retry of a
    /// transmission whose outcome we did not learn must ATTACH to the first one rather than
    /// becoming a second piece.
    /// </para>
    /// </summary>
    internal static IdempotencyKey ForClaimDocument(
        Guid tenantId, Guid connectionId, Guid claimDocumentId)
        => For(tenantId, connectionId, CommandType.AddClaimDocument,
            IntegrationEntityTypes.Claim, claimDocumentId);

    /// <summary>
    /// Every discriminator is rendered with the invariant culture. A date formatted with the
    /// process culture would key the same subscription differently on a fr-FR host and on an
    /// invariant one — the exact class of bug the spreadsheet readers of this repo already carry
    /// a rule about.
    /// </summary>
    private static string Canonicalise(object? discriminator) => discriminator switch
    {
        null => string.Empty,
        string s => s.Trim(),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Guid g => g.ToString("N"),
        // Enums land here and render as their NAME, not their numeric value: renumbering an enum
        // must not silently re-key every pending command.
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => discriminator.ToString() ?? string.Empty,
    };
}
