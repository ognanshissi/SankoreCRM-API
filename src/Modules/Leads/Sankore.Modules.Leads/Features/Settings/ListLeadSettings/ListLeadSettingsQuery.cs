namespace Sankore.Modules.Leads.Features.Settings.ListLeadSettings;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Every M13 parameter of the caller's tenant. A query — no <c>ICommand</c>, so neither the
/// transaction nor the audit behaviour runs.
/// </summary>
internal sealed record ListLeadSettingsQuery : IRequest<Result<IReadOnlyList<LeadSettingDto>>>;
