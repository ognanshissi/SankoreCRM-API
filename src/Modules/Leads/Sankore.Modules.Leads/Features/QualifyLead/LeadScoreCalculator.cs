namespace Sankore.Modules.Leads.Features.QualifyLead;

using System.Text.Json;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// Computes a 0-100 qualification score from lead attributes.
/// Scoring breakdown:
///   Data completeness  0-25 pts
///   Intent level       0-30 pts
///   Source quality     0-20 pts
///   Desired amount     0-15 pts
///   Location/agency    0-10 pts
/// </summary>
internal sealed class LeadScoreCalculator
{
    public (int Score, string FactorsJson) Calculate(Lead lead)
    {
        var completeness = DataCompletenessScore(lead);
        var intent       = IntentScore(lead);
        var source       = SourceScore(lead);
        var amount       = DesiredAmountScore(lead);
        var location     = LocationScore(lead);

        var total = Math.Clamp(completeness + intent + source + amount + location, 0, 100);

        var factors = new
        {
            dataCompleteness = completeness,
            intent           = intent,
            source           = source,
            desiredAmount    = amount,
            location         = location,
            total            = total
        };

        return (total, JsonSerializer.Serialize(factors));
    }

    private static int DataCompletenessScore(Lead lead)
    {
        int pts = 0;
        if (!string.IsNullOrWhiteSpace(lead.FirstName) && !string.IsNullOrWhiteSpace(lead.LastName)) pts += 5;
        if (!string.IsNullOrWhiteSpace(lead.Email))      pts += 5;
        if (lead.DateOfBirth.HasValue)                   pts += 5;
        if (lead.DesiredAmount is not null)               pts += 5;
        if (!string.IsNullOrWhiteSpace(lead.CompanyName)) pts += 5;
        return pts; // max 25
    }

    private static int IntentScore(Lead lead) => lead.IntentLevel switch
    {
        LeadIntentLevel.Hot     => 30,
        LeadIntentLevel.Warm    => 20,
        LeadIntentLevel.Cold    => 10,
        _                       => 0
    };

    private static int SourceScore(Lead lead) => lead.Source switch
    {
        LeadSource.Agency      => 20,   // in-branch capture — high intent
        LeadSource.CallCenter  => 18,
        LeadSource.Referral    => 18,
        LeadSource.WhatsApp    => 15,
        LeadSource.MobileAgent => 15,
        LeadSource.Web         => 12,
        LeadSource.Partner     => 12,
        LeadSource.Sms         => 10,
        LeadSource.Ussd        => 10,
        LeadSource.Campaign    => 10,
        LeadSource.FileImport  => 5,
        _                      => 8
    };

    private static int DesiredAmountScore(Lead lead)
    {
        if (lead.DesiredAmount is null) return 0;
        return lead.DesiredAmount.Amount > 0 ? 15 : 5;
    }

    private static int LocationScore(Lead lead)
    {
        int pts = 0;
        if (lead.Location is not null)         pts += 5;
        if (lead.PreferredAgencyId.HasValue)   pts += 5;
        return pts; // max 10
    }
}
