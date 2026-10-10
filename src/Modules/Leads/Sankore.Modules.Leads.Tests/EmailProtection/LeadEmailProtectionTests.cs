namespace Sankore.Modules.Leads.Tests.EmailProtection;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers e-mail protection end to end (BNF-B-01): the column holds ciphertext, the blind index
/// stays searchable, and a lead captured before the migration keeps matching until the backfill
/// reaches it.
/// </summary>
public sealed class LeadEmailProtectionTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private static readonly ILeadEmailIndexer Indexer = TestLeadEmailIndexer.Instance;

    private Lead Capture(string? email, string phone = "+2250700000001")
        => Lead.Capture(
            tenantId: _tenant,
            fullName: "Aya Kouassi",
            phoneNumber: phone,
            source: LeadSource.Agency,
            interestedProduct: "Crédit commerçant",
            preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System,
            email: email,
            emailBlindIndex: Indexer.Compute(email));

    // ── Index determinism ─────────────────────────────────────────────────

    [Theory]
    [InlineData("aya@example.ci", "AYA@EXAMPLE.CI")]
    [InlineData("aya@example.ci", " aya@example.ci ")]
    public void The_same_address_indexes_the_same_whatever_its_casing_or_padding(string a, string b)
    {
        // Dedup is an equality test on this value, so the normalisation has to agree with
        // MatchProbe.EmailNorm — trim plus lower-case. If it did not, an address typed with a
        // capital would be stored under one index and searched for under another.
        Indexer.Compute(a).Should().Be(Indexer.Compute(b));
    }

    [Fact]
    public void Two_different_addresses_index_differently()
    {
        Indexer.Compute("aya@example.ci").Should().NotBe(Indexer.Compute("kofi@example.ci"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_address_has_no_index(string? email)
    {
        // Not the index of an empty string: that one value would be shared by every lead with no
        // e-mail, making them all each other's duplicates.
        Indexer.Compute(email).Should().BeNull();
    }

    // ── Encryption at rest ────────────────────────────────────────────────

    [Fact]
    public async Task The_email_column_holds_ciphertext_and_the_property_reads_back_clear()
    {
        using var factory = new TestDbContextFactory(_tenant);

        await using (var write = factory.CreateContext())
        {
            write.Leads.Add(Capture("aya@example.ci"));
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = factory.CreateContext();
        var lead = await read.Leads.SingleAsync(CancellationToken.None);

        // Through EF the value converter reverses the encryption, so the domain sees clear text.
        lead.Email.Should().Be("aya@example.ci");

        // And the stored form is a v1 AES-GCM payload, not the address. Asserted through the
        // encryptor rather than by reading the raw column, which EF InMemory does not expose:
        // encrypting the same address again must produce a DIFFERENT payload, which is only true
        // of a real non-deterministic cipher.
        var first  = LeadsTestEncryptor.Instance.Encrypt("aya@example.ci");
        var second = LeadsTestEncryptor.Instance.Encrypt("aya@example.ci");
        first.Should().StartWith("v1:").And.NotContain("aya@example.ci");
        first.Should().NotBe(second, "a deterministic payload would make the column a lookup key");
    }

    // ── Dedup through the blind index ─────────────────────────────────────

    [Fact]
    public async Task A_lead_is_found_by_its_email_through_the_blind_index()
    {
        using var factory = new TestDbContextFactory(_tenant);

        await using (var write = factory.CreateContext())
        {
            write.Leads.Add(Capture("aya@example.ci"));
            await write.SaveChangesAsync(CancellationToken.None);
        }

        var probeIndex = Indexer.Compute("AYA@EXAMPLE.CI");

        await using var read = factory.CreateContext();
        var found = await read.Leads
            .Where(l => l.EmailBlindIndex == probeIndex)
            .ToListAsync(CancellationToken.None);

        found.Should().HaveCount(1,
            "the index is the only searchable half; the encrypted column cannot be compared");
    }

    [Fact]
    public async Task Comparing_the_encrypted_column_works_here_and_would_NOT_work_on_postgresql()
    {
        // READ THIS BEFORE TRUSTING THE SUITE ON ENCRYPTED COLUMNS.
        //
        // Measured, not assumed: on EF InMemory this predicate FINDS the row. The provider
        // materialises the entity, the value converter decrypts, and the comparison happens in
        // memory on clear text. On Npgsql the same expression is translated to SQL, the literal is
        // pushed through the converter on the way down — encrypted with a FRESH nonce — and the
        // comparison is between two unrelated ciphertexts, which never match.
        //
        // So the most dangerous mistake available here is one this suite cannot catch: a query
        // comparing Lead.Email passes every test and returns nothing in production. That is why
        // the rule is a convention enforced by reading — CaptureLeadHandler, ConvertLeadHandler
        // and FindDuplicatesHandler all go through EmailBlindIndex — and why this case exists at
        // all: to state the gap rather than leave someone to find it.
        using var factory = new TestDbContextFactory(_tenant);

        await using (var write = factory.CreateContext())
        {
            write.Leads.Add(Capture("aya@example.ci"));
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = factory.CreateContext();
        var found = await read.Leads
            .Where(l => l.Email == "aya@example.ci")
            .ToListAsync(CancellationToken.None);

        found.Should().HaveCount(1,
            "the in-memory provider decrypts then compares — PostgreSQL would compare ciphertexts "
            + "and find nothing, so this result is a warning, not a guarantee");
    }

    // ── The transitional legacy column ────────────────────────────────────

    [Fact]
    public async Task The_backfill_protects_a_pre_migration_lead()
    {
        using var factory = new TestDbContextFactory(_tenant);
        Guid leadId;

        await using (var write = factory.CreateContext())
        {
            // A lead as the migration leaves it: the clear value in the legacy column, both new
            // columns empty. Captured with no e-mail, then the legacy column set directly.
            var lead = Capture(email: null);
            leadId = lead.Id;
            write.Leads.Add(lead);
            await write.SaveChangesAsync(CancellationToken.None);

            write.Entry(lead).Property(nameof(Lead.LegacyEmailClear)).CurrentValue = "aya@example.ci";
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using (var run = factory.CreateContext())
        {
            await LeadEmailProtectionBackfill.RunAsync(
                run, Indexer, NullLogger.Instance, CancellationToken.None);
        }

        await using var read = factory.CreateContext();
        var migrated = await read.Leads.SingleAsync(l => l.Id == leadId, CancellationToken.None);

        migrated.Email.Should().Be("aya@example.ci");
        migrated.EmailBlindIndex.Should().Be(Indexer.Compute("aya@example.ci"));
    }

    [Fact]
    public async Task The_backfill_is_idempotent_and_leaves_protected_leads_alone()
    {
        using var factory = new TestDbContextFactory(_tenant);

        await using (var write = factory.CreateContext())
        {
            write.Leads.Add(Capture("aya@example.ci"));
            await write.SaveChangesAsync(CancellationToken.None);
        }

        for (var run = 0; run < 3; run++)
        {
            await using var ctx = factory.CreateContext();
            await LeadEmailProtectionBackfill.RunAsync(
                ctx, Indexer, NullLogger.Instance, CancellationToken.None);
        }

        await using var read = factory.CreateContext();
        var lead = await read.Leads.SingleAsync(CancellationToken.None);

        lead.Email.Should().Be("aya@example.ci");
        lead.EmailBlindIndex.Should().Be(Indexer.Compute("aya@example.ci"));
    }

    [Fact]
    public async Task The_backfill_clears_a_blank_legacy_value_so_it_stops_being_reselected()
    {
        // Without this the row is picked up on every boot for ever — which is the loop
        // MaxBatchesPerRun exists to survive, and this is the fix for the cause.
        using var factory = new TestDbContextFactory(_tenant);

        await using (var write = factory.CreateContext())
        {
            var blank = Capture(email: null);
            write.Leads.Add(blank);
            await write.SaveChangesAsync(CancellationToken.None);

            write.Entry(blank).Property(nameof(Lead.LegacyEmailClear)).CurrentValue = "   ";
            await write.SaveChangesAsync(CancellationToken.None);
        }

        await using (var run = factory.CreateContext())
        {
            await LeadEmailProtectionBackfill.RunAsync(
                run, Indexer, NullLogger.Instance, CancellationToken.None);
        }

        await using var read = factory.CreateContext();
        var lead = await read.Leads.SingleAsync(CancellationToken.None);

        lead.LegacyEmailClear.Should().BeNull();
        lead.EmailBlindIndex.Should().BeNull();
        lead.Email.Should().BeNull();
    }
}
