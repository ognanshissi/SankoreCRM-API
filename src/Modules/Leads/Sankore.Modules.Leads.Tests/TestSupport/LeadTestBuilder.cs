namespace Sankore.Modules.Leads.Tests.TestSupport;

using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Test data builder for Lead. Keeps test arrange sections readable and
/// resilient to constructor changes — a common companion pattern to
/// Vertical Slice testing.
/// </summary>
public sealed class LeadTestBuilder
{
    private Guid _tenantId = Guid.NewGuid();
    private string _fullName = "Awa Ndiaye";
    private string _phone = "+221771234567";
    private LeadSource _source = LeadSource.Web;
    private string _product = "Crédit individuel";
    private string _language = "Wolof";
    private GeoPoint _location = new(14.6928, -17.4467); // Dakar Plateau
    private Guid? _agencyId;

    public static LeadTestBuilder Create() => new();

    public LeadTestBuilder WithLanguage(string language)
    {
        _language = language;
        return this;
    }

    public LeadTestBuilder WithProduct(string product)
    {
        _product = product;
        return this;
    }

    public LeadTestBuilder WithLocation(double lat, double lng)
    {
        _location = new GeoPoint(lat, lng);
        return this;
    }

    public LeadTestBuilder WithTenant(Guid tenantId)
    {
        _tenantId = tenantId;
        return this;
    }

    public LeadTestBuilder WithAgency(Guid agencyId)
    {
        _agencyId = agencyId;
        return this;
    }

    public Lead Build()
    {
        var lead = Lead.Capture(
            tenantId: _tenantId,
            fullName: _fullName,
            phoneNumber: _phone,
            source: _source,
            interestedProduct: _product,
            preferredLanguage: _language,
            location: _location,
            preferredAgencyId: _agencyId,
            clock: TimeProvider.System);

        lead.Qualify(75); // moves to SalesQualified by default for dispatching tests
        lead.ClearDomainEvents();
        return lead;
    }
}
