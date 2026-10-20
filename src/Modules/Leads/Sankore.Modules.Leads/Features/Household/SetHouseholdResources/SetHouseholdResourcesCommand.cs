namespace Sankore.Modules.Leads.Features.Household.SetHouseholdResources;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Declares what the household lives on per month — net income, housing, other loans — so an agent
/// can estimate what it can repay before the qualification questionnaire.
///
/// <para>
/// A full replacement, unlike the enrichment: the three figures are one declaration, read together
/// as a ratio, and a screen that posted only the income would leave last month's rent standing
/// next to it. Null clears that figure.
/// </para>
/// </summary>
internal sealed record SetHouseholdResourcesCommand(
    Guid LeadId,
    decimal? MonthlyIncome,
    decimal? HousingCost,
    decimal? OtherDebts,
    string? Currency = null,
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
