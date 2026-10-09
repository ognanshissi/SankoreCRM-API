namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// ASS-03's third criterion: « Un produit peut être lié à un produit de crédit (assurance
/// emprunteur) ».
///
/// <para>
/// Reached through <c>IAdministrationModule.GetProductCategoryAsync</c> — a PublicApi contract,
/// never M12's assembly, and never a foreign key across a schema.
/// </para>
/// </summary>
public sealed class LinkedCreditProductCheckTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly TestIntegrationDbContextFactory _factory = new(Tenant);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task No_linked_credit_product_is_the_ordinary_case_and_passes()
    {
        var check = InsuranceTestHarness.CreditCheck(Tenant);

        (await check.VerifyAsync(null, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await check.VerifyAsync("   ", CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_loan_product_is_accepted()
    {
        var check = InsuranceTestHarness.CreditCheck(
            Tenant, InsuranceTestHarness.Administration("CRED-001", "Loan"));

        (await check.VerifyAsync("CRED-001", CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_credit_product_code_M12_does_not_know_is_refused()
    {
        var administration = Substitute.For<IAdministrationModule>();
        administration
            .GetProductCategoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await InsuranceTestHarness
            .CreditCheck(Tenant, administration)
            .VerifyAsync("CRED-INEXISTANT", CancellationToken.None);

        result.Error.Should().StartWith(IntegrationErrors.InsuranceLinkedCreditProductInvalid);
    }

    [Fact]
    public async Task A_savings_product_is_refused_because_a_borrowers_insurance_needs_a_loan()
    {
        var result = await InsuranceTestHarness
            .CreditCheck(Tenant, InsuranceTestHarness.Administration("EPG-001", "Savings"))
            .VerifyAsync("EPG-001", CancellationToken.None);

        // Not a typo an agent would catch: the subscription screen would offer life cover beside a
        // passbook, and the eligibility rule « détention d'un crédit » would then be checked
        // against a product that can never satisfy it. The detail names the category so the
        // administrator knows what they picked.
        result.Error.Should().StartWith(IntegrationErrors.InsuranceLinkedCreditProductInvalid);
        result.Error.Should().Contain("Savings");
    }

    [Fact]
    public async Task The_category_comparison_is_case_insensitive()
    {
        // M12's own column is free text ("Loan", "loan"): a case-sensitive comparison here would
        // refuse a perfectly good configuration, and the repo has already paid for exactly that
        // mistake with email-template locales (see LanguageCode in the kernel).
        var result = await InsuranceTestHarness
            .CreditCheck(Tenant, InsuranceTestHarness.Administration("CRED-001", "loan"))
            .VerifyAsync("CRED-001", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_product_naming_a_non_loan_credit_product_is_not_created()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        await using var db = _factory.CreateContext();

        var result = await new CreateInsuranceProductHandler(
                db,
                InsuranceTestHarness.CreditCheck(
                    Tenant, InsuranceTestHarness.Administration("EPG-001", "Savings")),
                InsuranceTestHarness.Pricing(db),
                new FixedTenantContext(Tenant),
                InsuranceTestHarness.User(Tenant),
                InsuranceTestHarness.Clock())
            .Handle(
                new CreateInsuranceProductCommand(
                    connection.Id, "ASS-VIE-EMP",
                    InsuranceTestHarness.Body(linkedCreditProductCode: "EPG-001")),
                CancellationToken.None);

        result.Error.Should().StartWith(IntegrationErrors.InsuranceLinkedCreditProductInvalid);

        // Checked BEFORE the write, so nothing is persisted. A check after the insert would leave
        // the row behind on a rolled-back transaction in any caller that swallows the result.
        await using var read = _factory.CreateContext();
        (await read.InsuranceProducts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_borrowers_insurance_product_keeps_its_credit_product_CODE_and_no_foreign_key()
    {
        await using var seed = _factory.CreateContext();
        var connection = InsuranceTestHarness.SeedConnection(seed, Tenant);

        await using var db = _factory.CreateContext();

        var result = await new CreateInsuranceProductHandler(
                db,
                InsuranceTestHarness.CreditCheck(Tenant),
                InsuranceTestHarness.Pricing(db),
                new FixedTenantContext(Tenant),
                InsuranceTestHarness.User(Tenant),
                InsuranceTestHarness.Clock())
            .Handle(
                new CreateInsuranceProductCommand(
                    connection.Id, "ASS-VIE-EMP",
                    InsuranceTestHarness.Body(linkedCreditProductCode: "CRED-001")),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        // A string code, in the `integration` schema, pointing at a row in `administration`. The
        // link is validated once at write time and degrades to "unlinked" afterwards — the rule
        // this repo applies to LeadAssignment.RuleId and IntegrationConnection.RelayAgentId.
        result.Value!.LinkedCreditProductCode.Should().Be("CRED-001");
    }
}
