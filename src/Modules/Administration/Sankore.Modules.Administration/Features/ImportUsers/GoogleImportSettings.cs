namespace Sankore.Modules.Administration.Features.ImportUsers;

public sealed class GoogleImportSettings
{
    public string ServiceAccountKeyPath { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = "SankoreCRM";
}
