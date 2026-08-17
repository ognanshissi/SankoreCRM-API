using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Leads.Tests.TestSupport;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

public sealed class AgentTestBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _fullName = "Test Agent";
    private Guid _agencyId = Guid.NewGuid();
    private List<string> _languages = new() { "FR" };
    private List<string> _specialties = new();
    private GeoPoint _location = new(14.6928, -17.4467);
    private int _activeLeads;
    private int _hotLeads;
    private double _conversionRate = 0.5;
    private bool _isAvailable = true;

    public static AgentTestBuilder Create() => new();

    public AgentTestBuilder SpeakingLanguages(params string[] languages)
    {
        _languages = languages.ToList();
        return this;
    }

    public AgentTestBuilder SpecializedIn(params string[] specialties)
    {
        _specialties = specialties.ToList();
        return this;
    }

    public AgentTestBuilder LocatedAt(double lat, double lng)
    {
        _location = new GeoPoint(lat, lng);
        return this;
    }

    public AgentTestBuilder WithLoad(int activeLeads)
    {
        _activeLeads = activeLeads;
        return this;
    }

    public AgentTestBuilder WithHotLeads(int hotLeads)
    {
        _hotLeads = hotLeads;
        return this;
    }

    public AgentTestBuilder WithConversionRate(double rate)
    {
        _conversionRate = rate;
        return this;
    }

    public AgentTestBuilder Named(string name)
    {
        _fullName = name;
        return this;
    }

    public AgentTestBuilder Unavailable()
    {
        _isAvailable = false;
        return this;
    }

    public AgentSummary Build() => new(
        Id: _id,
        FullName: _fullName,
        AgencyId: _agencyId,
        SpokenLanguages: _languages,
        Specialties: _specialties,
        CurrentLocation: _location,
        ActiveLeadsCount: _activeLeads,
        HotLeadsCount: _hotLeads,
        ConversionRate30d: _conversionRate,
        IsAvailable: _isAvailable);
}
