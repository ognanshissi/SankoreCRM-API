namespace Sankore.Modules.Leads.Tests.Features.UpdateLead;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.UpdateLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The desired amount is in the institution's currency (owner, 2026-10-24) — and an amount typed
/// before the administrator changed it stays editable in its own.
/// </summary>
public sealed class InstitutionCurrencyTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IInstitutionCurrency _currency = Substitute.For<IInstitutionCurrency>();
    private readonly Lead _lead;

    public InstitutionCurrencyTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        using var db = _factory.CreateContext();
        db.Leads.Add(_lead);
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    private void Institution(string code) =>
        _currency.GetAsync(_tenant, Arg.Any<CancellationToken>()).Returns(code);

    private async Task<Result> SetAmountAsync(decimal amount, string? currency)
    {
        await using var db = _factory.CreateContext();
        return await new UpdateLeadHandler(db, TestLeadEmailIndexer.Instance,
                Substitute.For<IAdministrationModule>(), NullLogger<UpdateLeadHandler>.Instance, _currency)
            .Handle(new UpdateLeadCommand(_lead.Id, null, null, null, null, null, null, null,
                amount, currency, null, null, null, null, null, null), default);
    }

    private async Task<string?> StoredCurrencyAsync()
    {
        await using var db = _factory.CreateContext();
        return (await db.Leads.SingleAsync(l => l.Id == _lead.Id)).DesiredAmount?.Currency;
    }

    [Fact]
    public async Task An_amount_without_a_currency_takes_the_institution_one()
    {
        Institution("EUR");

        (await SetAmountAsync(1_000, null)).IsSuccess.Should().BeTrue();

        (await StoredCurrencyAsync()).Should().Be("EUR");
    }

    [Fact]
    public async Task Another_currency_is_refused()
    {
        Institution("XOF");

        (await SetAmountAsync(1_000, "USD")).Error.Should().Be(InstitutionCurrencies.NotInstitution);
    }

    [Fact]
    public async Task An_amount_typed_before_a_change_keeps_its_currency_when_edited()
    {
        Institution("XOF");
        (await SetAmountAsync(500_000, "XOF")).IsSuccess.Should().BeTrue();

        Institution("EUR");
        var edited = await SetAmountAsync(600_000, "XOF");

        edited.IsSuccess.Should().BeTrue(edited.Error);
        (await StoredCurrencyAsync()).Should().Be("XOF");
    }
}
