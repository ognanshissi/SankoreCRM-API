namespace Sankore.Shared.Infrastructure.Google;

/// <summary>
/// Service-account credentials for the Google import readers (Sheets, Contacts).
/// Shared: every module that imports from Google authenticates the same way,
/// and modules may not reference each other's types.
/// </summary>
public sealed class GoogleImportSettings
{
    public string ServiceAccountKeyPath { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = "SankoreCRM";
}
