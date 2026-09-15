namespace Sankore.Modules.Administration.Features.Agencies.CreateAgency;

internal interface IAgencyCodeGenerator
{
    Task<string> NextCodeAsync(CancellationToken ct = default);
}
