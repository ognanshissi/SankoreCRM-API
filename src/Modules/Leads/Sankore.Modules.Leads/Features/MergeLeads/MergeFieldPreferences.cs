namespace Sankore.Modules.Leads.Features.MergeLeads;

/// <summary>
/// Declares which optional scalar fields should be taken from the SOURCE lead
/// and applied to the TARGET lead during a merge.
/// Fields set to false (default) keep the target's existing value.
/// Fields set to true are overwritten with the source's value (if the source
/// has a non-null value for that field).
/// </summary>
public sealed record MergeFieldPreferences(
    bool TakeEmail             = false,
    bool TakeFirstName         = false,
    bool TakeLastName          = false,
    bool TakeNationalId        = false,
    bool TakeCustomerReference = false,
    bool TakeDateOfBirth       = false,
    bool TakeGender            = false,
    bool TakeDesiredAmount     = false,
    bool TakeComment           = false,
    bool TakeCompanyName       = false,
    bool TakeCompanyEmail      = false,
    bool TakeCompanyPhone      = false,
    bool TakeWebsite           = false,
    bool TakeLocation          = false
)
{
    /// <summary>Returns the names of fields that will be taken from source.</summary>
    public IReadOnlyList<string> OverriddenFields()
    {
        var list = new List<string>();
        if (TakeEmail)             list.Add("email");
        if (TakeFirstName)         list.Add("firstName");
        if (TakeLastName)          list.Add("lastName");
        if (TakeNationalId)        list.Add("nationalId");
        if (TakeCustomerReference) list.Add("customerReference");
        if (TakeDateOfBirth)       list.Add("dateOfBirth");
        if (TakeGender)            list.Add("gender");
        if (TakeDesiredAmount)     list.Add("desiredAmount");
        if (TakeComment)           list.Add("comment");
        if (TakeCompanyName)       list.Add("companyName");
        if (TakeCompanyEmail)      list.Add("companyEmail");
        if (TakeCompanyPhone)      list.Add("companyPhone");
        if (TakeWebsite)           list.Add("website");
        if (TakeLocation)          list.Add("location");
        return list;
    }
}
