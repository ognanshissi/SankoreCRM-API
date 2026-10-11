namespace Sankore.Modules.Leads.Features.Settings.GetLeadSetting;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetLeadSettingQuery(string Key) : IRequest<Result<LeadSettingDto>>;
