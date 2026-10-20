using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Persistence;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

/// <param name="encryptor">
/// M13's keyed encryptor. Applied by hand from <see cref="LeadsDbContext"/> and excluded from the
/// assembly scan, because a scanned configuration has no way to receive it.
/// </param>
internal sealed class LeadConfiguration(IFieldEncryptor encryptor) : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        builder.ToTable("leads");

        builder.HasKey(l => l.Id);

        // Required scalars
        builder.Property(l => l.FullName).HasMaxLength(200).IsRequired();
        builder.Property(l => l.PhoneNumber).HasMaxLength(30).IsRequired();
        builder.Property(l => l.PhoneBlindIndex).HasMaxLength(64);
        builder.Property(l => l.InterestedProduct).HasMaxLength(100).IsRequired();
        builder.Property(l => l.PreferredLanguage).HasMaxLength(50).IsRequired();

        // Optional identity
        builder.Property(l => l.FirstName).HasMaxLength(100);
        builder.Property(l => l.LastName).HasMaxLength(100);

        // ── E-mail: encrypted at rest, searchable through a blind index (BNF-B-01) ──────────
        //
        // The CLR property stays plaintext and the COLUMN holds AES-256-GCM ciphertext, so no
        // handler can forget to encrypt and every existing read-side projection keeps working.
        // Unbounded text: a ~200-character address becomes roughly 300 characters of payload, and
        // a cap here would truncate ciphertext into something that no longer decrypts.
        //
        // NOTHING may compare this column in SQL. The ciphertext is non-deterministic, so
        // `l.Email == x` compares two unrelated ciphertexts and quietly matches nothing.
        builder.Property(l => l.Email)
               .HasColumnName("email_enc")
               .HasConversion(EncryptedConverters.Text(encryptor));

        // The deterministic half: this is what dedup queries read.
        builder.Property(l => l.EmailBlindIndex).HasMaxLength(64);
        builder.HasIndex(l => new { l.TenantId, l.EmailBlindIndex })
               .HasFilter("email_blind_index IS NOT NULL");

        // TRANSITIONAL — the pre-encryption column, kept so leads captured before the migration
        // keep matching by e-mail until BackfillLeadEmailProtectionJob has filled their index.
        // Dropped together with the fallback branches once the backfill reports zero rows left.
        builder.Property(l => l.LegacyEmailClear)
               .HasColumnName("email")
               .HasMaxLength(200);

        // ── Existing-customer link (BNF-B-22) ───────────────────────────────────────────────
        //
        // CustomerId has no .Property() mapping and needs none: a plain Guid? becomes a nullable
        // uuid under the module's snake-case convention, as for LeadSourceConfigId and
        // LeadAssignment.RuleId. It carries NO foreign key — the client is in another schema.
        builder.Property(l => l.CustomerLinkReason).HasMaxLength(500);
        builder.Property(l => l.CustomerLinkIsAutomatic).HasDefaultValue(false);

        // Serves ILeadsModule.GetOpenLeadsForCustomerAsync, which the client record calls to list
        // a customer's requests in progress. Partial: the overwhelming majority of leads are new
        // prospects with no customer, and no query wants those.
        builder.HasIndex(l => new { l.TenantId, l.CustomerId })
               .HasFilter("customer_id IS NOT NULL");

        // Kind is READ FROM CustomerId, never stored — see LeadKind for why a column would be a
        // second source of truth the conversion branches on.
        builder.Ignore(l => l.Kind);

        // ── The principal's postal identity (BNF-B-02) ──────────────────────────────────────
        //
        // Unprefixed column names for the two that already existed (first_name, last_name) and
        // "principal_" for the new ones, matching the DDL of the specification.
        builder.Property(l => l.PrincipalUsageName).HasMaxLength(100);
        builder.Property(l => l.PrincipalPostalCode).HasMaxLength(20);
        builder.Property(l => l.PrincipalCity).HasMaxLength(100);
        builder.Property(l => l.PrincipalCountryCode).HasMaxLength(2).IsFixedLength(false);
        builder.Property(l => l.PrincipalNeighbourhood).HasMaxLength(100);

        builder.Property(l => l.HouseholdMonthlyIncome).HasPrecision(18, 4);
        builder.Property(l => l.HouseholdHousingCost).HasPrecision(18, 4);
        builder.Property(l => l.HouseholdOtherDebts).HasPrecision(18, 4);
        builder.Property(l => l.HouseholdResourcesCurrency).HasMaxLength(3);

        // Encrypted, like every other postal address. Unbounded text: the ciphertext of a
        // ~200-character address is roughly 300 characters, and a cap would truncate a payload
        // into something that no longer decrypts. NOTHING may compare this column in SQL.
        builder.Property(l => l.PrincipalAddressLine)
               .HasColumnName("principal_address_enc")
               .HasConversion(EncryptedConverters.Text(encryptor));

        // Same treatment, same reason: a landmark next to a neighbourhood is an address.
        builder.Property(l => l.PrincipalLandmark)
               .HasColumnName("principal_landmark_enc")
               .HasConversion(EncryptedConverters.Text(encryptor));

        // ── Household and PEP (BNF-B-03, BNF-B-05) ──────────────────────────────────────────
        builder.OwnsPepStatus(l => l.PrincipalPep, prefix: "principal_pep_");

        // No index: nothing filters on WHEN a declaration was made, only on whether there is one,
        // and that question is asked per lead by the completeness check.
        builder.Property(l => l.PepDeclaredAt);

        builder.Property(l => l.HasPepExposure).HasDefaultValue(false);

        // Indexed because dispatching filters on it: BNF-B-05 routes an exposed lead to a cleared
        // agent, which is a WHERE over this column. Partial — exposure is the small minority.
        builder.HasIndex(l => new { l.TenantId, l.HasPepExposure })
               .HasFilter("has_pep_exposure");

        builder.HasMany(l => l.HouseholdMembers)
               .WithOne()
               .HasForeignKey(m => m.LeadId)
               // Cascade so the row disappears with the lead it belongs to. It is NOT how a member
               // is removed — that is RemovedAt, a logical removal (BNF-B-03) — this only covers
               // a lead genuinely being deleted.
               .OnDelete(DeleteBehavior.Cascade);

        // A projection over the flat identity columns, not a stored member.
        builder.Ignore(l => l.PrincipalIdentity);
        builder.Ignore(l => l.ActiveHouseholdMembers);
        builder.Ignore(l => l.IsHouseholdEditable);

        // Organisation
        builder.Property(l => l.CompanyName).HasMaxLength(200);
        builder.Property(l => l.CompanyEmail).HasMaxLength(200);
        builder.Property(l => l.CompanyPhone).HasMaxLength(30);
        builder.Property(l => l.Website).HasMaxLength(300).IsRequired(false);

        // Enums stored as strings
        builder.Property(l => l.ProspectType).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.Source).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.PipelineStage).HasConversion<string>().HasMaxLength(40);
        builder.Property(l => l.Gender).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.IntentLevel).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.Channel).HasConversion<string>().HasMaxLength(30);

        // Identity fields for duplicate detection
        builder.Property(l => l.NationalId).HasMaxLength(50);
        builder.Property(l => l.CustomerReference).HasMaxLength(100);
        builder.HasIndex(l => new { l.TenantId, l.NationalId })
               .HasFilter("national_id IS NOT NULL");
        builder.HasIndex(l => new { l.TenantId, l.CustomerReference })
               .HasFilter("customer_reference IS NOT NULL");

        // The dispatch queue, read every few minutes by DispatchWaitingLeadsJob, oldest first.
        // Partial: the queue is a handful of rows among every lead the tenant ever captured.
        builder.HasIndex(l => new { l.TenantId, l.AwaitingDispatchSince })
               .HasFilter("awaiting_dispatch_since IS NOT NULL");

        // Capture context
        builder.Property(l => l.Campaign).HasMaxLength(100);
        builder.Property(l => l.ExternalReference).HasMaxLength(100);
        builder.Property(l => l.Comment).HasMaxLength(2000);

        // Dynamic forms: the tenant's custom fields. jsonb so a later lot can filter on a value
        // (`custom_fields ->> 'segment'`) without a migration of the data.
        builder.Property(l => l.CustomFieldsJson).HasColumnName("custom_fields").HasColumnType("jsonb");

        // List filters on custom values are jsonb containments (`custom_fields @> '{"k":"v"}'`);
        // jsonb_path_ops is the GIN operator class built for exactly that operator, smaller and
        // faster than the default one, which also serves key-existence queries nothing here runs.
        builder.HasIndex(l => l.CustomFieldsJson)
            .HasMethod("gin")
            .HasOperators("jsonb_path_ops")
            .HasDatabaseName("ix_leads_custom_fields");
        builder.Property(l => l.LossReason).HasMaxLength(500);

        // Money owned type
        builder.OwnsOne(l => l.DesiredAmount, m =>
        {
            m.Property(p => p.Amount)
                .HasColumnName("desired_amount")
                .HasPrecision(18, 4);
            m.Property(p => p.Currency)
                .HasColumnName("desired_currency")
                .HasMaxLength(3);
        });

        // Acquisition cost — snapshot from source at capture time
        builder.OwnsOne(l => l.AcquisitionCost, m =>
        {
            m.Property(p => p.Amount).HasColumnName("acquisition_cost").HasPrecision(18, 4);
            m.Property(p => p.Currency).HasColumnName("acquisition_currency").HasMaxLength(3);
        });

        // Attribution & UTM
        builder.Property(l => l.UtmSource).HasMaxLength(200);
        builder.Property(l => l.UtmMedium).HasMaxLength(100);
        builder.Property(l => l.UtmCampaign).HasMaxLength(200);
        builder.Property(l => l.UtmContent).HasMaxLength(200);
        builder.Property(l => l.UtmTerm).HasMaxLength(200);
        builder.Property(l => l.LandingPage).HasMaxLength(2000);
        builder.Property(l => l.Referrer).HasMaxLength(2000);
        builder.Property(l => l.ExternalId).HasMaxLength(200);
        builder.Property(l => l.SocialPublicationId).HasMaxLength(200);
        builder.Property(l => l.SocialInteractionId).HasMaxLength(200);

        // Owned value object
        builder.OwnsOne(l => l.Location, loc =>
        {
            loc.Property(p => p.Latitude).HasColumnName("lat");
            loc.Property(p => p.Longitude).HasColumnName("lng");
        });

        // Indexes
        builder.HasIndex(l => new { l.TenantId, l.Status });
        builder.HasIndex(l => new { l.TenantId, l.PipelineStage });
        builder.HasIndex(l => l.PhoneNumber);
        builder.HasIndex(l => new { l.TenantId, l.PhoneBlindIndex })
               .HasFilter("phone_blind_index IS NOT NULL");
        builder.HasIndex(l => l.FullName);
        builder.HasIndex(l => l.ExpiresAt);

        // Serves the LeadSourceConfigId filter on ListLeads. Partial on purpose: leads typed in
        // the UI or imported from a file carry no source config, they are the majority, and no
        // query wants them — so imports pay no index-write cost. The dispatching resolver reads
        // this column by lead primary key and needs no index of its own.
        // No .Property() mapping: a plain Guid? maps to a nullable uuid under the module's
        // snake-case convention, which is why LeadAssignment.RuleId has no entry either.
        builder.HasIndex(l => new { l.TenantId, l.LeadSourceConfigId })
               .HasFilter("lead_source_config_id IS NOT NULL");

        // ── Retention (BNF-B-19) ────────────────────────────────────────────────────────────
        builder.Property(l => l.IsAnonymized).HasDefaultValue(false);

        // Serves the anonymisation job's selection, which is the only query that reads it: closed
        // leads, not yet anonymised, older than the cutoff. Partial on the flag rather than on the
        // status, because the flag is what SHRINKS — every run removes rows from this index, and an
        // index over anonymised leads would only grow.
        builder.HasIndex(l => new { l.TenantId, l.UpdatedAt })
               .HasFilter("NOT is_anonymized")
               .HasDatabaseName("ix_leads_retention_candidates");

        builder.Ignore(l => l.RetentionClockStartedAt);

        // Domain events are transient, never persisted.
        builder.Ignore(l => l.DomainEvents);
    }
}
