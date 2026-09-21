namespace Sankore.Modules.Administration.Features.ImportUsers;

/// <summary>
/// Normalized row from any import source (CSV, Excel, Google Sheets, Google Contacts).
/// Maps 1:1 to a CreateUserCommand dispatch.
/// </summary>
public sealed record ImportUserRow
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? AgencyCode { get; init; }
    public string? RoleCode { get; init; }
    public string DefaultLanguage { get; init; } = "fr";
    public string? SpokenLanguages { get; init; }
    public string? Specialties { get; init; }
}
