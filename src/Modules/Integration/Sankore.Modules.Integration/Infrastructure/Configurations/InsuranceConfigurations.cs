namespace Sankore.Modules.Integration.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Integration.Domain;

// ─────────────────────────────────────────────────────────────────────────────
// The insurance family's tables (ASS-03 → ASS-12).
//
// NAMING — prefix `ins_`.
//   ASS-01 says « Seules les tables propres au core banking gardent le préfixe cbs_ ». The
//   sentence reserves `cbs_` for core banking; it does not say a second family must go unprefixed,
//   and leaving these bare would merge them with the socle tables that genuinely serve both
//   families. `ins_` is the spec's own abbreviation — ASS-11 names the permissions `Ins.Product.
//   Manage`, `Ins.Policy.View` — so it is a vocabulary the specification already uses rather than
//   one invented here, and it mirrors `cbs_` in shape: a three-letter family marker. `\dt
//   integration.*` then reads as exactly three groups: `integration_` (socle, family-agnostic),
//   `cbs_` (core banking only), `ins_` (insurance only).
//
// FOREIGN KEYS — intra-schema only, and all `Restrict` but one.
//   No key ever crosses a schema: the customer is an opaque `CrmId`, M12's credit product an
//   opaque CODE, M01 and M02 are never referenced physically. Inside `integration` a key is right
//   wherever a dangling id would make a row unreadable rather than merely unprovenanced — a
//   subscription naming a product that does not exist cannot be priced, read or justified.
//   `Restrict` everywhere because NOTHING in this schema is ever deleted: a connection
//   deactivates, a product is withdrawn, a policy takes a status, a claim takes a status. A
//   cascade could therefore only ever express a deletion nobody intends, and would amplify it
//   silently; `Restrict` turns the same mistake into an error. The single exception is
//   `ins_statement_line → ins_statement`, where deleting a DRAFT statement and regenerating it is
//   a real operation and its lines must go with it.
//
//   Three links are deliberately NOT keys: `ins_subscription`'s three command ids,
//   `ins_statement.transmit_batch_file_id`, and `ins_policy.subscription_id`. The first two point
//   at rows a retention job may purge long after the financial record must survive — a cascade
//   there would take the money trail with the log. The third would close a cycle with
//   `ins_subscription.policy_id`; of the two directions only the subscription's matters (it is
//   what "the subscription succeeded, here is the contract" reads), so that one is the key and the
//   back-pointer is informational.
//
// ENCRYPTED COLUMNS — `text`, never a bounded type.
//   `ins_subscription.beneficiaries_encrypted`, `ins_medical_questionnaire.answers_encrypted`,
//   `ins_consent_proof.evidence_encrypted` and `ins_claim.description_encrypted` hold
//   `v1:nonce:tag:ciphertext` from this module's KEYED `IFieldEncryptor`
//   (`IntegrationFieldProtection.Key`). The payload grows with the plaintext, so a length cap
//   would truncate a long value into bytes that no longer decrypt — the reason
//   `integration_command.payload_encrypted` is `text` too.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class InsuranceProductConfiguration : IEntityTypeConfiguration<InsuranceProduct>
{
    public void Configure(EntityTypeBuilder<InsuranceProduct> b)
    {
        b.ToTable("ins_product");
        b.HasKey(p => p.Id);

        b.Property(p => p.InsurerProductCode).HasMaxLength(100).IsRequired();
        b.Property(p => p.Name).HasMaxLength(200).IsRequired();
        b.Property(p => p.Description).HasMaxLength(2000);
        b.Property(p => p.Periodicity).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(p => p.PricingMode).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(p => p.MinKycLevel).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(p => p.Currency).HasMaxLength(3);
        b.Property(p => p.LinkedCreditProductCode).HasMaxLength(100);

        b.Property(p => p.FixedPremiumAmount).HasPrecision(18, 2);
        b.Property(p => p.InsuredAmount).HasPrecision(18, 2);

        // A rate, not an amount: four decimals so 0.1250 is exact and a quarter-point is
        // expressible. Rounding the COMMISSION is the statement line's job.
        b.Property(p => p.CommissionRate).HasPrecision(5, 4);

        // Read whole, to render one product. jsonb for the reason CbsSnapshot gives: nothing
        // queries across products' guarantees, and a child table would cost a
        // delete-and-reinsert on every edit for joins nobody performs.
        b.Property(p => p.GuaranteesJson)
            .HasColumnName("guarantees")
            .HasColumnType("jsonb")
            .IsRequired();

        b.Property(p => p.Version).IsRowVersion();

        b.HasOne<IntegrationConnection>()
            .WithMany()
            .HasForeignKey(p => p.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        // One catalogue entry per insurer product per insurer. A business invariant as an index
        // and not a read-then-write: two administrators configuring the same product at once would
        // both pass an in-memory check and the agent screen would then show it twice, with two
        // prices and two commission rates. All three columns are NOT NULL, so PostgreSQL's
        // NULLS-DISTINCT default cannot quietly admit a duplicate.
        b.HasIndex(p => new { p.TenantId, p.ConnectionId, p.InsurerProductCode })
            .IsUnique()
            .HasDatabaseName("ux_ins_product_insurer_code");

        // The catalogue screen, and the offerability projection: this tenant's products for one
        // insurer, active first.
        b.HasIndex(p => new { p.TenantId, p.ConnectionId, p.IsActive })
            .HasDatabaseName("ix_ins_product_tenant_connection_active");

        // Borrower's insurance: "which insurance products are attached to this credit product"
        // is how a loan screen offers one. Filtered, because most products have no linked credit.
        b.HasIndex(p => new { p.TenantId, p.LinkedCreditProductCode })
            .HasFilter("linked_credit_product_code IS NOT NULL")
            .HasDatabaseName("ix_ins_product_linked_credit");

        b.Ignore(p => p.DomainEvents);
    }
}

internal sealed class InsuranceSubscriptionConfiguration : IEntityTypeConfiguration<InsuranceSubscription>
{
    public void Configure(EntityTypeBuilder<InsuranceSubscription> b)
    {
        b.ToTable("ins_subscription");
        b.HasKey(s => s.Id);

        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(s => s.Currency).HasMaxLength(3).IsRequired();
        b.Property(s => s.PremiumAmount).HasPrecision(18, 2);

        // External references, in clear and bounded, like integration_reference.external_id: a
        // transaction reference is a reference, not a credential, and both the reversal and the
        // statement read them.
        b.Property(s => s.CbsAccountRef).HasMaxLength(200);
        b.Property(s => s.CbsDebitReference).HasMaxLength(200);
        b.Property(s => s.CbsReversalReference).HasMaxLength(200);
        b.Property(s => s.InsurerPolicyReference).HasMaxLength(200);

        b.Property(s => s.FailureCode).HasMaxLength(80);
        b.Property(s => s.FailureDetail).HasMaxLength(1000);

        // Beneficiaries: names, relationships and dates of birth of third parties. Encrypted.
        b.Property(s => s.BeneficiariesEncrypted).HasColumnType("text");

        b.Property(s => s.Version).IsRowVersion();

        b.HasOne<IntegrationConnection>()
            .WithMany()
            .HasForeignKey(s => s.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<InsuranceProduct>()
            .WithMany()
            .HasForeignKey(s => s.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<PolicyRecord>()
            .WithMany()
            .HasForeignKey(s => s.PolicyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // ASS-04's idempotency, « par (client, produit, date d'effet) », as a DATABASE guarantee.
        //
        // The index and not a read-then-write, for the reason the spec cares about: the thing a
        // second row would cause is a SECOND PREMIUM DEBIT on a customer's account. Two clicks on
        // the agent's button, or a retried HTTP request, both reach the handler concurrently and
        // both pass any in-memory check; the unique violation is what makes the retry attach to
        // the existing saga instead. Same instrument as
        // ux_integration_command_tenant_idempotency and ux_kyc_files_open_per_customer.
        //
        // The connection is NOT in the key and does not need to be: product_id determines the
        // insurer, so the same CRM-visible product offered by two insurers is two product rows and
        // two keys. All four columns are NOT NULL, which is what makes this index safe without
        // NULLS NOT DISTINCT — the trap that had to be repaired on
        // ux_integration_reconciliation_gap_open, where two of four gap types leave a column null
        // by contract and PostgreSQL therefore accepted identical rows.
        b.HasIndex(s => new { s.TenantId, s.CrmCustomerId, s.ProductId, s.EffectiveDate })
            .IsUnique()
            .HasDatabaseName("ux_ins_subscription_idempotency");

        // The one query that must never be an approximation: every subscription whose premium the
        // institution owes back (status = 'PremiumRefundDue'). See SubscriptionStatus for why that
        // cannot be inferred from "the last command is Rejected".
        b.HasIndex(s => new { s.TenantId, s.Status })
            .HasDatabaseName("ix_ins_subscription_tenant_status");

        // ASS-10's monthly statement: the adhésions of one insurer in one period.
        b.HasIndex(s => new { s.TenantId, s.ConnectionId, s.CreatedAt })
            .HasDatabaseName("ix_ins_subscription_tenant_connection_created");

        // The chain's hot path. The consumer that advances the saga arrives holding a COMMAND id
        // and nothing else — "the debit succeeded", "the insurer refused" — so each of the three
        // gets its own filtered index. Filtered because the columns are null until their step
        // starts, and most rows never reach the third.
        b.HasIndex(s => s.DebitCommandId)
            .HasFilter("debit_command_id IS NOT NULL")
            .HasDatabaseName("ix_ins_subscription_debit_command");

        b.HasIndex(s => s.SubscribeCommandId)
            .HasFilter("subscribe_command_id IS NOT NULL")
            .HasDatabaseName("ix_ins_subscription_subscribe_command");

        b.HasIndex(s => s.ReversalCommandId)
            .HasFilter("reversal_command_id IS NOT NULL")
            .HasDatabaseName("ix_ins_subscription_reversal_command");

        b.Ignore(s => s.DomainEvents);
    }
}

internal sealed class ConsentProofConfiguration : IEntityTypeConfiguration<ConsentProof>
{
    public void Configure(EntityTypeBuilder<ConsentProof> b)
    {
        b.ToTable("ins_consent_proof");
        b.HasKey(c => c.Id);

        b.Property(c => c.Channel).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(c => c.ConsentTextVersion).HasMaxLength(60).IsRequired();
        b.Property(c => c.ConsentTextSha256).HasMaxLength(64);
        b.Property(c => c.EvidenceStorageRef).HasMaxLength(500);
        b.Property(c => c.EvidenceSha256).HasMaxLength(64);
        b.Property(c => c.ContentType).HasMaxLength(100);

        // The evidence, when it is held inline rather than as an object.
        b.Property(c => c.EvidenceEncrypted).HasColumnType("text");

        // NO xmin. Evidence is written once and never edited, so there is no second writer to lose
        // a race with — see the entity's remarks.

        // Restrict, emphatically. A cascade here would let deleting a subscription destroy the
        // CIMA 2024 consent proof, which is the one row in this schema whose retention is a legal
        // obligation rather than an operational preference.
        b.HasOne<InsuranceSubscription>()
            .WithMany()
            .HasForeignKey(c => c.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        // One proof per subscription. Both columns NOT NULL.
        b.HasIndex(c => new { c.TenantId, c.SubscriptionId })
            .IsUnique()
            .HasDatabaseName("ux_ins_consent_proof_subscription");

        // A compliance search starts from the customer, not from the subscription.
        b.HasIndex(c => new { c.TenantId, c.CrmCustomerId, c.ConsentedAt })
            .HasDatabaseName("ix_ins_consent_proof_customer");

        // ASS-04 attaches the proof to the COMMAND; this is the route from one to the other.
        b.HasIndex(c => c.CommandId)
            .HasFilter("command_id IS NOT NULL")
            .HasDatabaseName("ix_ins_consent_proof_command");

        b.Ignore(c => c.DomainEvents);
    }
}

internal sealed class MedicalQuestionnaireConfiguration : IEntityTypeConfiguration<MedicalQuestionnaire>
{
    public void Configure(EntityTypeBuilder<MedicalQuestionnaire> b)
    {
        b.ToTable("ins_medical_questionnaire");
        b.HasKey(q => q.Id);

        b.Property(q => q.QuestionnaireCode).HasMaxLength(60).IsRequired();

        // The whole reason this table exists. Encrypted, `text`, and the only column of the schema
        // that holds medical data — which is what makes ASS-12's "dedicated permission, traced"
        // enforceable on exactly one query path.
        b.Property(q => q.AnswersEncrypted).HasColumnType("text").IsRequired();

        // No xmin: like the consent proof, written once.

        b.HasOne<InsuranceSubscription>()
            .WithMany()
            .HasForeignKey(q => q.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(q => new { q.TenantId, q.SubscriptionId })
            .IsUnique()
            .HasDatabaseName("ux_ins_medical_questionnaire_subscription");

        // Deliberately NO index on (tenant_id, crm_customer_id): an index exists to make a query
        // cheap, and "every medical questionnaire of this customer" is not a query this module
        // should make convenient. The subscription is the documented route in.

        b.Ignore(q => q.DomainEvents);
    }
}

internal sealed class PolicyRecordConfiguration : IEntityTypeConfiguration<PolicyRecord>
{
    public void Configure(EntityTypeBuilder<PolicyRecord> b)
    {
        b.ToTable("ins_policy");
        b.HasKey(p => p.Id);

        b.Property(p => p.ExternalPolicyId).HasMaxLength(200).IsRequired();
        b.Property(p => p.PolicyNumber).HasMaxLength(100).IsRequired();
        b.Property(p => p.InsurerProductCode).HasMaxLength(100).IsRequired();
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(p => p.StatusDetail).HasMaxLength(500);
        b.Property(p => p.Periodicity).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        b.Property(p => p.PremiumAmount).HasPrecision(18, 2);
        b.Property(p => p.InsuredAmount).HasPrecision(18, 2);

        b.Property(p => p.Version).IsRowVersion();

        b.HasOne<IntegrationConnection>()
            .WithMany()
            .HasForeignKey(p => p.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable: ASS-07 synchronises policies sold at the insurer's own counter, whose product
        // the tenant's catalogue may not hold. Inventing a catalogue row to satisfy a key would
        // put a product nobody configured in front of an agent.
        b.HasOne<InsuranceProduct>()
            .WithMany()
            .HasForeignKey(p => p.ProductId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // SubscriptionId carries NO foreign key: it would close a cycle with
        // ins_subscription.policy_id, and of the two directions only that one is load-bearing.

        // What makes ASS-07's synchronisation an upsert rather than a duplicator: one row per
        // insurer-side policy per connection. Per connection and not per tenant, because two
        // insurers may legitimately issue the same contract number.
        b.HasIndex(p => new { p.TenantId, p.ConnectionId, p.ExternalPolicyId })
            .IsUnique()
            .HasDatabaseName("ux_ins_policy_external");

        // The counter's own question: this customer's contracts.
        b.HasIndex(p => new { p.TenantId, p.CrmCustomerId, p.Status })
            .HasDatabaseName("ix_ins_policy_customer_status");

        // ASS-08's daily job: which live policies have an instalment coming. Filtered, because a
        // cancelled or expired policy has no next due date and most of the table eventually is.
        b.HasIndex(p => new { p.TenantId, p.NextDueDate })
            .HasFilter("next_due_date IS NOT NULL")
            .HasDatabaseName("ix_ins_policy_next_due");

        // ASS-10's statement: one insurer's movements over a period.
        b.HasIndex(p => new { p.TenantId, p.ConnectionId, p.StatusChangedAt })
            .HasDatabaseName("ix_ins_policy_connection_status_changed");

        b.HasIndex(p => p.SubscriptionId)
            .HasFilter("subscription_id IS NOT NULL")
            .HasDatabaseName("ix_ins_policy_subscription");

        b.Ignore(p => p.DomainEvents);
    }
}

internal sealed class PolicyCertificateConfiguration : IEntityTypeConfiguration<PolicyCertificate>
{
    public void Configure(EntityTypeBuilder<PolicyCertificate> b)
    {
        b.ToTable("ins_policy_certificate");
        b.HasKey(c => c.Id);

        b.Property(c => c.InsurerCertificateRef).HasMaxLength(200);
        b.Property(c => c.FileName).HasMaxLength(260).IsRequired();
        b.Property(c => c.ContentType).HasMaxLength(100).IsRequired();
        b.Property(c => c.Sha256).HasMaxLength(64).IsRequired();
        b.Property(c => c.StorageRef).HasMaxLength(500).IsRequired();

        // No bytes column, and no xmin: the document is an immutable encrypted object in the
        // module's file store, and this row describes it. See the entity's remarks.

        b.HasOne<PolicyRecord>()
            .WithMany()
            .HasForeignKey(c => c.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The current attestation is the most recent row — several per policy are deliberate, so a
        // renewal does not destroy the one a customer was handed last year.
        b.HasIndex(c => new { c.TenantId, c.PolicyId, c.FetchedAt })
            .HasDatabaseName("ix_ins_policy_certificate_policy_fetched");

        // The same document fetched twice is one row. Digest over the PLAINTEXT, NOT NULL.
        b.HasIndex(c => new { c.TenantId, c.PolicyId, c.Sha256 })
            .IsUnique()
            .HasDatabaseName("ux_ins_policy_certificate_digest");

        b.Ignore(c => c.DomainEvents);
    }
}

internal sealed class PremiumInstalmentConfiguration : IEntityTypeConfiguration<PremiumInstalment>
{
    public void Configure(EntityTypeBuilder<PremiumInstalment> b)
    {
        b.ToTable("ins_premium_instalment");
        b.HasKey(i => i.Id);

        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(i => i.Currency).HasMaxLength(3).IsRequired();
        b.Property(i => i.Amount).HasPrecision(18, 2);
        b.Property(i => i.CbsDebitReference).HasMaxLength(200);
        b.Property(i => i.LastErrorCode).HasMaxLength(80);
        b.Property(i => i.LastErrorDetail).HasMaxLength(500);

        b.Property(i => i.Version).IsRowVersion();

        b.HasOne<PolicyRecord>()
            .WithMany()
            .HasForeignKey(i => i.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);

        // What makes ASS-08's daily job safe to run twice. Hangfire guarantees at-least-once, so a
        // retried morning run would otherwise debit the same due date a second time. All three
        // columns NOT NULL.
        b.HasIndex(i => new { i.TenantId, i.PolicyId, i.DueDate })
            .IsUnique()
            .HasDatabaseName("ux_ins_premium_instalment_due");

        // The orchestrator's scan: what is due, across tenants. Deliberately NOT prefixed by
        // tenant_id, exactly like ix_integration_command_status_next_attempt — the orchestrator
        // asks "which tenants owe work" before it knows the tenant.
        b.HasIndex(i => new { i.Status, i.DueDate, i.NextAttemptAt })
            .HasDatabaseName("ix_ins_premium_instalment_due_attempt");

        // The impayés screen, per tenant.
        b.HasIndex(i => new { i.TenantId, i.Status, i.DueDate })
            .HasDatabaseName("ix_ins_premium_instalment_tenant_status");

        b.HasIndex(i => i.DebitCommandId)
            .HasFilter("debit_command_id IS NOT NULL")
            .HasDatabaseName("ix_ins_premium_instalment_debit_command");

        b.Ignore(i => i.DomainEvents);
    }
}

internal sealed class ClaimRecordConfiguration : IEntityTypeConfiguration<ClaimRecord>
{
    public void Configure(EntityTypeBuilder<ClaimRecord> b)
    {
        b.ToTable("ins_claim");
        b.HasKey(c => c.Id);

        b.Property(c => c.Nature).HasMaxLength(100).IsRequired();
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(c => c.StatusDetail).HasMaxLength(500);
        b.Property(c => c.MissingDocuments).HasMaxLength(1000);
        b.Property(c => c.ExternalClaimId).HasMaxLength(200);
        b.Property(c => c.ClaimNumber).HasMaxLength(100);
        b.Property(c => c.IndemnityCurrency).HasMaxLength(3);
        b.Property(c => c.IndemnityCbsReference).HasMaxLength(200);
        b.Property(c => c.IndemnityAmount).HasPrecision(18, 2);

        // The agent's account of the loss: free text about a named person's misfortune. Encrypted
        // for the reason ASS-12 encrypts the pièces that go with it.
        b.Property(c => c.DescriptionEncrypted).HasColumnType("text");

        b.Property(c => c.Version).IsRowVersion();

        b.HasOne<IntegrationConnection>()
            .WithMany()
            .HasForeignKey(c => c.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<PolicyRecord>()
            .WithMany()
            .HasForeignKey(c => c.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);

        // The declaration's own idempotency, mirroring IdempotencyKeyFactory.ForClaimDeclaration
        // exactly — policy, date of loss, nature — so a double-click produces one claim and not
        // one claim per click against one command. It refuses nothing the command key does not
        // already refuse, and that is the point: the two cannot drift.
        b.HasIndex(c => new { c.TenantId, c.PolicyId, c.OccurredOn, c.Nature })
            .IsUnique()
            .HasDatabaseName("ux_ins_claim_declaration");

        // The sync's upsert key. PARTIAL on purpose: the column is null until the insurer answers,
        // and PostgreSQL's NULLS-DISTINCT default would admit those rows anyway — relying on a
        // default to mean "allowed" is accidental, so the exemption is written down.
        b.HasIndex(c => new { c.TenantId, c.ConnectionId, c.ExternalClaimId })
            .IsUnique()
            .HasFilter("external_claim_id IS NOT NULL")
            .HasDatabaseName("ux_ins_claim_external");

        // The counter: this customer's dossiers, most recent first.
        b.HasIndex(c => new { c.TenantId, c.CrmCustomerId, c.DeclaredAt })
            .HasDatabaseName("ix_ins_claim_customer_declared");

        // The follow-up screen, and the sync's own worklist: what is still open.
        b.HasIndex(c => new { c.TenantId, c.Status })
            .HasDatabaseName("ix_ins_claim_tenant_status");

        b.HasIndex(c => c.DeclareCommandId)
            .HasFilter("declare_command_id IS NOT NULL")
            .HasDatabaseName("ix_ins_claim_declare_command");

        b.Ignore(c => c.DomainEvents);
    }
}

internal sealed class ClaimDocumentConfiguration : IEntityTypeConfiguration<ClaimDocument>
{
    public void Configure(EntityTypeBuilder<ClaimDocument> b)
    {
        b.ToTable("ins_claim_document");
        b.HasKey(d => d.Id);

        b.Property(d => d.DocumentKind).HasMaxLength(60).IsRequired();
        b.Property(d => d.FileName).HasMaxLength(260).IsRequired();
        b.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
        b.Property(d => d.Sha256).HasMaxLength(64).IsRequired();
        b.Property(d => d.StorageRef).HasMaxLength(500).IsRequired();
        b.Property(d => d.ScanStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(d => d.ScanDetail).HasMaxLength(300);

        b.Property(d => d.Version).IsRowVersion();

        // Restrict, not Cascade: a pièce de sinistre is evidence ASS-12 names, and no delete of a
        // dossier should take it quietly.
        b.HasOne<ClaimRecord>()
            .WithMany()
            .HasForeignKey(d => d.ClaimId)
            .OnDelete(DeleteBehavior.Restrict);

        // The same file uploaded twice for one dossier is refused rather than sent to the insurer
        // twice. Digest over the PLAINTEXT, NOT NULL.
        b.HasIndex(d => new { d.TenantId, d.ClaimId, d.Sha256 })
            .IsUnique()
            .HasDatabaseName("ux_ins_claim_document_digest");

        // The antivirus worklist (ASS-09, criterion 1): what has not been scanned. Nothing may
        // reach the insurer before this queue is empty for it.
        b.HasIndex(d => new { d.TenantId, d.ScanStatus })
            .HasDatabaseName("ix_ins_claim_document_scan_status");

        b.Ignore(d => d.DomainEvents);
    }
}

internal sealed class InsurerStatementConfiguration : IEntityTypeConfiguration<InsurerStatement>
{
    public void Configure(EntityTypeBuilder<InsurerStatement> b)
    {
        b.ToTable("ins_statement");
        b.HasKey(s => s.Id);

        // YYYY-MM, the shape integration_kyc_limit_alert.period already uses.
        b.Property(s => s.Period).HasMaxLength(7).IsRequired();
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(s => s.Currency).HasMaxLength(3).IsRequired();
        b.Property(s => s.PremiumCollected).HasPrecision(18, 2);
        b.Property(s => s.PremiumReversed).HasPrecision(18, 2);
        b.Property(s => s.CommissionAmount).HasPrecision(18, 2);
        b.Property(s => s.CsvStorageRef).HasMaxLength(500);
        b.Property(s => s.PdfStorageRef).HasMaxLength(500);
        b.Property(s => s.TransmitFailureDetail).HasMaxLength(1000);

        b.Property(s => s.Version).IsRowVersion();

        b.HasOne<IntegrationConnection>()
            .WithMany()
            .HasForeignKey(s => s.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        // TransmitBatchFileId carries no foreign key: the batch socle purges old files on its own
        // retention, and a key would either block that purge or take the statement with it.

        // One bordereau per insurer per month, so a regeneration edits the draft instead of
        // producing a second document with different figures. All three columns NOT NULL.
        b.HasIndex(s => new { s.TenantId, s.ConnectionId, s.Period })
            .IsUnique()
            .HasDatabaseName("ux_ins_statement_period");

        b.HasIndex(s => new { s.TenantId, s.Period })
            .HasDatabaseName("ix_ins_statement_tenant_period");

        b.Ignore(s => s.DomainEvents);
    }
}

internal sealed class InsurerStatementLineConfiguration : IEntityTypeConfiguration<InsurerStatementLine>
{
    public void Configure(EntityTypeBuilder<InsurerStatementLine> b)
    {
        b.ToTable("ins_statement_line");
        b.HasKey(l => l.Id);

        b.Property(l => l.LineType).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(l => l.Currency).HasMaxLength(3).IsRequired();
        b.Property(l => l.Amount).HasPrecision(18, 2);
        b.Property(l => l.CommissionRate).HasPrecision(5, 4);
        b.Property(l => l.CommissionAmount).HasPrecision(18, 2);
        b.Property(l => l.PolicyNumber).HasMaxLength(100).IsRequired();
        b.Property(l => l.InsurerProductCode).HasMaxLength(100).IsRequired();
        b.Property(l => l.CbsReference).HasMaxLength(200);

        // No xmin: a line is written once. A draft statement is rebuilt by deleting its lines and
        // writing them again, which is a delete and an insert, never an update.

        // The ONE cascade of this schema. Deleting a draft statement and regenerating it is a real
        // operation, and its lines are meaningless without it.
        b.HasOne<InsurerStatement>()
            .WithMany()
            .HasForeignKey(l => l.StatementId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<PolicyRecord>()
            .WithMany()
            .HasForeignKey(l => l.PolicyId)
            .OnDelete(DeleteBehavior.Restrict);

        // What stops a second generation duplicating every line.
        //
        // NULLS NOT DISTINCT is load-bearing here, not a refinement — the same lesson
        // ux_integration_reconciliation_gap_open had to be rebuilt to learn. instalment_id is null
        // by contract on a subscription, a reversal and a cancellation line, and PostgreSQL treats
        // NULLs as DISTINCT by default: without this clause the index would constrain only the
        // premium lines, and those are exactly the control case that would make the hole look
        // closed.
        b.HasIndex(l => new { l.TenantId, l.StatementId, l.LineType, l.PolicyId, l.InstalmentId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_ins_statement_line");

        // Rendering one bordereau, in order.
        b.HasIndex(l => new { l.TenantId, l.StatementId, l.OccurredOn })
            .HasDatabaseName("ix_ins_statement_line_statement_occurred");

        b.Ignore(l => l.DomainEvents);
    }
}
