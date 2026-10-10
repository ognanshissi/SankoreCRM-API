namespace Sankore.Modules.Administration.Features.ComplianceSettings.GetComplianceSettings;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads the current tenant's compliance parameters. A query: it does NOT implement
/// <c>ICommand</c>, so it skips the transaction and audit behaviors.
/// </summary>
internal sealed record GetComplianceSettingsQuery : IRequest<Result<ComplianceSettingsResponse>>;
