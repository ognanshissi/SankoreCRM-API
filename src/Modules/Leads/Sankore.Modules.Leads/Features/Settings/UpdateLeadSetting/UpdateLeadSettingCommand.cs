namespace Sankore.Modules.Leads.Features.Settings.UpdateLeadSetting;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Writes one M13 parameter.
///
/// <para>
/// A mutation, so <see cref="ICommand"/> (transaction + audit) and <see cref="IResourceCommand"/>.
/// The audit entry is the point as much as the write: <c>lead-retention-months</c> governs an
/// IRREVERSIBLE nightly destruction, and "who shortened the retention period, and when" is the
/// first question asked after leads disappear. Nothing else in the system records it.
/// </para>
///
/// <para>
/// No property is marked sensitive: a retention period is not personal data, and the audit trail
/// deliberately keeps the value in clear — a masked entry would answer that question with "a
/// value was changed to ***".
/// </para>
/// </summary>
internal sealed record UpdateLeadSettingCommand(string Key, string Value)
    : IRequest<Result<LeadSettingDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSetting";
    public string? ResourceId => Key;
}

/// <summary>The key travels in the route, so the body carries the value alone.</summary>
internal sealed record UpdateLeadSettingRequest(string Value);
