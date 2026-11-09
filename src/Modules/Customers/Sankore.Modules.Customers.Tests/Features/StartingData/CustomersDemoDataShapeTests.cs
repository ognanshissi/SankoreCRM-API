namespace Sankore.Modules.Customers.Tests.Features.StartingData;

using FluentAssertions;
using Sankore.Modules.Customers.Features.StartingData;
using Sankore.Modules.Customers.Features.Complaints;
using Xunit;

/// <summary>
/// The demo's clients (lot 4): fictitious, distinct, adult, and the complaints filed on clients the
/// demo creates — a typo here would stop the whole load.
/// </summary>
public sealed class CustomersDemoDataShapeTests
{
    [Fact]
    public void Keys_are_unique_and_complaints_name_existing_clients()
    {
        var keys = CustomersDemoData.People.Select(p => p.Key).Concat(CustomersDemoData.Companies.Select(c => c.Key)).ToList();

        keys.Should().OnlyHaveUniqueItems();
        CustomersDemoData.Complaints.Select(c => c.Client).Should().BeSubsetOf(keys);
        keys.Should().Contain(CustomersDemoData.SocleComplaint.Client);
        ClientComplaintFiling.IsBankingCategory(CustomersDemoData.SocleComplaint.Category).Should().BeFalse();
    }

    [Fact]
    public void Clients_are_adults_on_numbers_no_prospect_uses()
    {
        CustomersDemoData.People.Should().OnlyContain(p => p.BirthDate < DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-18));
        var phones = Enumerable.Range(1, 15).Concat(Enumerable.Range(50, 5)).Select(CustomersDemoData.Phone).ToList();
        phones.Should().OnlyHaveUniqueItems().And.OnlyContain(p => p.StartsWith("+22507990"));
    }

    [Fact]
    public void Every_client_is_in_one_of_the_two_demo_branches()
        => CustomersDemoData.People.Select(p => p.Branch).Concat(CustomersDemoData.Companies.Select(c => c.Branch))
            .Distinct().Should().BeEquivalentTo("Agence Plateau", "Agence Yopougon");
}
