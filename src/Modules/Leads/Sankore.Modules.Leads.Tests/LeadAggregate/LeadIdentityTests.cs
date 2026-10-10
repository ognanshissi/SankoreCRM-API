// Namespace deliberately NOT Sankore.Modules.Leads.Tests.Domain: that would shadow
// Sankore.Modules.Leads.Domain inside this assembly, and a sibling test writing `Domain.Lead`
// would stop compiling. The same trap the Settings folder already avoided.
namespace Sankore.Modules.Leads.Tests.LeadAggregate;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers the identity changes of BNF-B-01: the derived <c>FullName</c>, the e-mail/blind-index
/// pair, and the product line reference.
/// </summary>
public sealed class LeadIdentityTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static Lead Capture(
        string? fullName = "Aya Kouassi",
        string? firstName = null,
        string? lastName = null,
        string? email = null,
        string? emailBlindIndex = null)
        => Lead.Capture(
            tenantId: Tenant,
            fullName: fullName!,
            phoneNumber: "+2250700000001",
            source: LeadSource.Agency,
            interestedProduct: "Crédit commerçant",
            preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System,
            firstName: firstName,
            lastName: lastName,
            email: email,
            emailBlindIndex: emailBlindIndex);

    // ── FullName is derived for a new lead (BNF-B-01) ─────────────────────

    [Fact]
    public void Structured_names_win_over_a_supplied_full_name()
    {
        var lead = Capture(fullName: "WHATEVER THE CALLER SENT", firstName: "Aya", lastName: "Kouassi");

        lead.FullName.Should().Be("Aya Kouassi");
        lead.FirstName.Should().Be("Aya");
        lead.LastName.Should().Be("Kouassi");
    }

    [Fact]
    public void A_full_name_alone_is_kept_as_it_is_and_never_split()
    {
        // The v1 ingest contract, a file import and a web form all send only this. Splitting
        // "Kouassi Aya N'Guessan" has no reading a machine can defend, so nothing tries.
        var lead = Capture(fullName: "Kouassi Aya N'Guessan");

        lead.FullName.Should().Be("Kouassi Aya N'Guessan");
        lead.FirstName.Should().BeNull();
        lead.LastName.Should().BeNull();
    }

    [Fact]
    public void One_structured_name_falls_back_to_the_supplied_full_name()
    {
        // Google Contacts hands back a given name with no family name. Composing " Aya" from the
        // half that exists is how a lead ends up named with a leading space.
        var lead = Capture(fullName: "Aya K.", firstName: "Aya");

        lead.FullName.Should().Be("Aya K.");
    }

    [Fact]
    public void One_structured_name_and_no_full_name_uses_the_name_that_exists()
    {
        Capture(fullName: null, lastName: "Kouassi").FullName.Should().Be("Kouassi");
    }

    [Fact]
    public void A_lead_with_no_name_at_all_is_refused()
    {
        var act = () => Capture(fullName: "   ");
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Updating_a_structured_name_recomputes_the_full_name()
    {
        var lead = Capture(firstName: "Aya", lastName: "Kouassi");

        lead.Update(
            fullName: "IGNORED", firstName: null, lastName: "N'Guessan", gender: null,
            dateOfBirth: null, interestedProduct: null, desiredAmount: null,
            preferredLanguage: null, campaign: null, comment: null, location: null,
            preferredAgencyId: null).IsSuccess.Should().BeTrue();

        lead.FullName.Should().Be("Aya N'Guessan",
            "FullName is read-only once a lead has both names — renaming means changing them");
    }

    [Fact]
    public void A_legacy_lead_without_structured_names_can_still_be_renamed_directly()
    {
        // "les leads existants gardent leur valeur, sans découpage automatique" — and must stay
        // editable, otherwise every pre-BNF-B lead becomes unrenameable.
        var lead = Capture(fullName: "Kouassi Aya");

        lead.Update(
            fullName: "Kouassi Aya N'Guessan", firstName: null, lastName: null, gender: null,
            dateOfBirth: null, interestedProduct: null, desiredAmount: null,
            preferredLanguage: null, campaign: null, comment: null, location: null,
            preferredAgencyId: null);

        lead.FullName.Should().Be("Kouassi Aya N'Guessan");
    }

    // ── E-mail moves as a pair ────────────────────────────────────────────

    [Fact]
    public void Capture_stores_the_email_with_its_index()
    {
        var lead = Capture(email: " Aya@Example.CI ", emailBlindIndex: "abc123");

        lead.Email.Should().Be("Aya@Example.CI", "trimmed, but the casing is the prospect's");
        lead.EmailBlindIndex.Should().Be("abc123");
    }

    [Fact]
    public void Capture_stores_no_index_when_there_is_no_email()
    {
        // An index on a blank e-mail would be one value shared by every lead without an address,
        // making them all duplicates of each other.
        var lead = Capture(email: "   ", emailBlindIndex: "abc123");

        lead.Email.Should().BeNull();
        lead.EmailBlindIndex.Should().BeNull();
    }

    [Fact]
    public void SetEmail_refuses_an_email_with_no_index()
    {
        var lead = Capture();

        var result = lead.SetEmail("aya@example.ci", blindIndex: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("EMAIL_BLIND_INDEX_REQUIRED");
        lead.Email.Should().BeNull("a refused change stores neither half of the pair");
    }

    [Fact]
    public void SetEmail_replaces_both_halves()
    {
        var lead = Capture(email: "old@example.ci", emailBlindIndex: "old-index");

        lead.SetEmail("new@example.ci", "new-index").IsSuccess.Should().BeTrue();

        lead.Email.Should().Be("new@example.ci");
        lead.EmailBlindIndex.Should().Be("new-index");
    }

    [Fact]
    public void SetEmail_with_null_clears_both_halves()
    {
        var lead = Capture(email: "old@example.ci", emailBlindIndex: "old-index");

        lead.SetEmail(null, null).IsSuccess.Should().BeTrue();

        lead.Email.Should().BeNull();
        lead.EmailBlindIndex.Should().BeNull("a stale index would match an address nobody holds");
    }

    // ── Product line ──────────────────────────────────────────────────────

    [Fact]
    public void A_captured_lead_has_no_product_line_until_one_is_resolved()
    {
        var lead = Capture();

        lead.ProductLineId.Should().BeNull();
        lead.InterestedProduct.Should().Be("Crédit commerçant",
            "the captured label is untouched — it is the other half of the pair");
    }

    [Fact]
    public void SetProductLine_stores_a_resolved_line()
    {
        var lead = Capture();
        var line = Guid.NewGuid();

        lead.SetProductLine(line).IsSuccess.Should().BeTrue();
        lead.ProductLineId.Should().Be(line);
    }

    [Fact]
    public void SetProductLine_treats_an_empty_guid_as_absent()
    {
        // Guid.Empty is what this API's GuidConverter produces from a malformed JSON guid, so it
        // must never be stored as though it referenced a product line.
        var lead = Capture();

        lead.SetProductLine(Guid.Empty);
        lead.ProductLineId.Should().BeNull();
    }
}
