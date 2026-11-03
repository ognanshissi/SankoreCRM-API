namespace Sankore.Modules.Leads.Tests.Features.UpdateLead;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.UpdateLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// The company of a lead is editable after capture (2026-11-02): a workflow « Formulaire » step that
/// requires « Site web » could otherwise never be approved. Null leaves a field, blank clears it.
/// </summary>
public sealed class UpdateLeadCompanyTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly Lead _lead;

    public UpdateLeadCompanyTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _lead = LeadTestBuilder.Create().WithTenant(_tenant).Build();
        using var db = _factory.CreateContext();
        db.Leads.Add(_lead);
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Lead> UpdateAsync(string? name, string? email, string? phone, string? website)
    {
        await using (var db = _factory.CreateContext())
        {
            var result = await new UpdateLeadHandler(db, TestLeadEmailIndexer.Instance,
                    Substitute.For<IAdministrationModule>(), NullLogger<UpdateLeadHandler>.Instance)
                .Handle(new UpdateLeadCommand(_lead.Id, null, null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null,
                    CompanyName: name, CompanyEmail: email, CompanyPhone: phone, Website: website), default);
            result.IsSuccess.Should().BeTrue(result.Error);
        }

        await using var verify = _factory.CreateContext();
        return await verify.Leads.SingleAsync(l => l.Id == _lead.Id);
    }

    [Fact]
    public async Task The_company_is_written_then_a_blank_clears_and_a_null_keeps()
    {
        var written = await UpdateAsync("Koné SARL", "contact@kone.ci", "+2252722000000", "https://kone.ci");
        written.Website.Should().Be("https://kone.ci");
        written.CompanyName.Should().Be("Koné SARL");

        var cleared = await UpdateAsync(null, "", null, "");
        cleared.Website.Should().BeNull();
        cleared.CompanyEmail.Should().BeEmpty("the column is NOT NULL: cleared means empty, never null");
        cleared.CompanyName.Should().Be("Koné SARL", "a field not sent is left alone");
    }
}
