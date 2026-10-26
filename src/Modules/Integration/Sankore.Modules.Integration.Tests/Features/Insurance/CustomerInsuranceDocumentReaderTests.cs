namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Insurance.CustomerDocuments;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// What the insurance family reports to the client record's « Documents » tab (product owner,
/// 2026-10-26): information sheets handed over, policy documents, claim pieces, consent proofs.
/// </summary>
public sealed class CustomerInsuranceDocumentReaderTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);
    private readonly TimeProvider _clock = InsuranceTestHarness.Clock();

    public void Dispose() => _factory.Dispose();

    private async Task<IReadOnlyList<CustomerDocumentEntry>> ReadAsync(Guid customer)
    {
        await using var db = _factory.CreateContext();
        return await CustomerInsuranceDocumentReader.ReadAsync(db, customer, CancellationToken.None);
    }

    /// <summary>A policy and a claim on it, for <paramref name="customer"/>.</summary>
    private async Task<(Guid PolicyId, Guid ClaimId)> SeedPolicyAsync(Guid customer)
    {
        await using var db = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(db, Tenant);
        var policy = PolicyRecord.Create(
            Tenant, connection.Id, customer, $"POL-{Guid.NewGuid():N}", "N°1", "ASS-VIE",
            PolicyStatus.Issued, new DateOnly(2026, 6, 1), 7_500m, "XOF", PremiumPeriodicity.Annual, _clock);
        var claim = ClaimRecord.Create(
            Tenant, connection.Id, policy.Id, customer, new DateOnly(2026, 6, 15), "Décès",
            InsuranceTestHarness.Actor, _clock);
        db.Policies.Add(policy);
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return (policy.Id, claim.Id);
    }

    [Fact]
    public async Task A_claim_piece_opens_only_once_the_antivirus_passed_it()
    {
        var (_, claimId) = await SeedPolicyAsync(InsuranceTestHarness.Customer);
        await using (var db = _factory.CreateContext())
        {
            var clean = ClaimDocument.Create(Tenant, claimId, "PoliceReport", "pv.pdf", "application/pdf", 10,
                new string('a', 64), "ref-1", InsuranceTestHarness.Actor, _clock);
            clean.RecordScan(DocumentScanStatus.Clean, null, _clock);
            var pending = ClaimDocument.Create(Tenant, claimId, "Invoice", "facture.pdf", "application/pdf", 10,
                new string('b', 64), "ref-2", InsuranceTestHarness.Actor, _clock);
            db.ClaimDocuments.AddRange(clean, pending);
            await db.SaveChangesAsync();
        }

        var pieces = await ReadAsync(InsuranceTestHarness.Customer);

        pieces.Should().HaveCount(2).And.OnlyContain(p => p.Kind == "ClaimPiece" && p.Direction == CustomerDocumentDirection.Collected);
        pieces.Single(p => p.FileName == "pv.pdf").ContentPath.Should().StartWith("integration/insurance/claims/");
        pieces.Single(p => p.FileName == "facture.pdf").ContentPath.Should().BeNull(
            "the endpoint refuses a piece the scanner has not passed, so the line must not offer it");
    }

    [Fact]
    public async Task A_policy_document_says_whether_it_was_handed_over()
    {
        var (policyId, _) = await SeedPolicyAsync(InsuranceTestHarness.Customer);
        await using (var db = _factory.CreateContext())
        {
            var sent = PolicyDocument.Upload(Tenant, policyId, InsuranceTestHarness.Customer,
                PolicyDocumentKind.GeneralConditions, "CG", "ref", "cg.pdf", "application/pdf",
                new string('c', 64), InsuranceTestHarness.Actor, _clock);
            sent.MarkSentToClient(InformationSheetChannel.HandedOnPaper, InsuranceTestHarness.Actor, _clock);
            var kept = PolicyDocument.Upload(Tenant, policyId, InsuranceTestHarness.Customer,
                PolicyDocumentKind.SpecialConditions, "CP", "ref", "cp.pdf", "application/pdf",
                new string('d', 64), InsuranceTestHarness.Actor, _clock);
            db.PolicyDocuments.AddRange(sent, kept);
            await db.SaveChangesAsync();
        }

        var documents = await ReadAsync(InsuranceTestHarness.Customer);

        documents.Should().OnlyContain(d => d.Direction == CustomerDocumentDirection.Delivered);
        var cg = documents.Single(d => d.FileName == "cg.pdf");
        cg.Status.Should().Be("Delivered");
        cg.Detail.Should().Be(nameof(InformationSheetChannel.HandedOnPaper));
        cg.Kind.Should().Be("PolicyDocument.GeneralConditions");
        documents.Single(d => d.FileName == "cp.pdf").Status.Should().Be("NotSent");
    }

    [Fact]
    public async Task An_information_sheet_handed_over_is_listed_with_its_version()
    {
        var holdingId = Guid.NewGuid();
        await using (var db = _factory.CreateContext())
        {
            var sheet = ProductInformationSheet.Create(Tenant, Guid.NewGuid(), 3, "ref", "fiche.pdf",
                "application/pdf", 100, new string('e', 64), InsuranceTestHarness.Actor, _clock);
            db.InformationSheets.Add(sheet);
            db.InformationSheetDeliveries.Add(InformationSheetDelivery.Record(
                Tenant, holdingId, InsuranceTestHarness.Customer, sheet, InformationSheetChannel.ShownOnScreen,
                "ACK-1", InsuranceTestHarness.Actor, _clock, retainUntil: null));
            await db.SaveChangesAsync();
        }

        var line = (await ReadAsync(InsuranceTestHarness.Customer)).Should().ContainSingle().Subject;

        line.Kind.Should().Be("InformationSheet");
        line.Direction.Should().Be(CustomerDocumentDirection.Delivered);
        line.OriginType.Should().Be("Holding");
        line.OriginId.Should().Be(holdingId);
        line.OriginLabel.Should().Be("Version 3");
        line.ContentPath.Should().StartWith("integration/insurance/information-sheets/");
    }

    [Fact]
    public async Task Another_client_s_documents_are_not_listed()
    {
        var (_, claimId) = await SeedPolicyAsync(Guid.NewGuid());
        await using (var db = _factory.CreateContext())
        {
            db.ClaimDocuments.Add(ClaimDocument.Create(Tenant, claimId, "PoliceReport", "autre.pdf", "application/pdf", 10,
                new string('f', 64), "ref", InsuranceTestHarness.Actor, _clock));
            await db.SaveChangesAsync();
        }

        (await ReadAsync(InsuranceTestHarness.Customer)).Should().BeEmpty();
    }
}
